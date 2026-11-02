[CmdletBinding()]
param(
    [string]$NpcapSdkDirectory = $env:NPCAP_SDK_DIR,
    [string]$OutputDirectory = '',
    [ValidatePattern('\A[0-9]+\.[0-9]+\.[0-9]+\z')][string]$Version = '0.5.1'
)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath($PSScriptRoot)
$debug=Join-Path $repository 'debug'
if(-not $OutputDirectory){$OutputDirectory=Join-Path $debug 'package'}
$package=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $package.StartsWith($debug+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $package)){throw 'Package staging must be a new directory under repository debug/.'}
New-Item -ItemType Directory -Path $debug,(Join-Path $debug 'temp') -Force | Out-Null
if(-not $NpcapSdkDirectory){
    $sdkRoot=Join-Path $debug 'sdk';New-Item -ItemType Directory -Path $sdkRoot -Force | Out-Null
    $archive=Join-Path $sdkRoot 'npcap-sdk-1.17.zip'
    if(-not (Test-Path -LiteralPath $archive)){Invoke-WebRequest -UseBasicParsing -Uri 'https://npcap.com/dist/npcap-sdk-1.17.zip' -OutFile $archive}
    if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne 'AB7E1A6CB8D07427E0071EC11BE380BD9D2BB19AE6AB3F5EDE0698C301638258'){throw 'Official Npcap SDK archive hash differs.'}
    $NpcapSdkDirectory=Join-Path $sdkRoot ('verified-'+[guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $archive -DestinationPath $NpcapSdkDirectory
}
if(-not (Test-Path -LiteralPath (Join-Path $NpcapSdkDirectory 'Lib/x64/wpcap.lib'))){throw 'Npcap SDK directory does not contain Lib/x64/wpcap.lib.'}
$previousTemp=$env:TEMP;$previousTmp=$env:TMP;$previousSdk=$env:NPCAP_SDK_DIR;$previousRustFlags=$env:CARGO_ENCODED_RUSTFLAGS
try {
    $env:TEMP=Join-Path $debug 'temp';$env:TMP=$env:TEMP;$env:NPCAP_SDK_DIR=[IO.Path]::GetFullPath($NpcapSdkDirectory)
    $cargoCache=if($env:CARGO_HOME){[IO.Path]::GetFullPath($env:CARGO_HOME)}else{Join-Path ([Environment]::GetFolderPath('UserProfile')) '.cargo'}
    # Encoded arguments preserve spaces; release diagnostics never embed this PC's source/cache paths.
    $env:CARGO_ENCODED_RUSTFLAGS=@('-C','target-feature=+crt-static','--remap-path-prefix',($repository+'=/source/NVIDIA-SHIELD-P2570-Windows'),'--remap-path-prefix',($cargoCache+'=/cargo')) -join [char]31
    & cargo fmt --manifest-path (Join-Path $repository 'Cargo.toml') --all --check
    if($LASTEXITCODE -ne 0){throw 'Rust formatting check failed.'}
    & cargo test --locked --manifest-path (Join-Path $repository 'Cargo.toml') --target x86_64-pc-windows-msvc --target-dir (Join-Path $debug 'rust')
    if($LASTEXITCODE -ne 0){throw 'Rust tests failed.'}
    & cargo build --locked --release --manifest-path (Join-Path $repository 'Cargo.toml') --target x86_64-pc-windows-msvc --target-dir (Join-Path $debug 'rust')
    if($LASTEXITCODE -ne 0){throw 'Data host build failed.'}
    New-Item -ItemType Directory -Path $package,(Join-Path $package 'bin/data-host'),(Join-Path $package 'licenses') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $debug 'rust/x86_64-pc-windows-msvc/release/shield-controller-datahost.exe') -Destination (Join-Path $package 'bin/data-host')
    foreach($component in @(@('owner/ShieldControllerOwner.csproj','bin/owner'),@('tray/ShieldController.csproj','bin/tray'))){
        $name=($component[0] -split '/')[0]
        $intermediate=(Join-Path $debug ('build/'+$name+'/obj/'))
        & dotnet publish (Join-Path $repository $component[0]) -c Release -r win-x64 --self-contained true -o (Join-Path $package $component[1]) -p:DebugType=None -p:DebugSymbols=false -p:CopyOutputSymbolsToPublishDirectory=false "-p:BaseIntermediateOutputPath=$intermediate" "-p:MSBuildProjectExtensionsPath=$intermediate" "-p:RestorePackagesPath=$(Join-Path $debug 'dotnet-packages')" "-p:BaseOutputPath=$(Join-Path $debug ('build/'+$name+'/bin/'))"
        if($LASTEXITCODE -ne 0){throw ('Component publish failed: '+$component[0])}
    }
    & (Join-Path $package 'bin/owner/ShieldControllerOwner.exe') --mode selftest
    if($LASTEXITCODE -ne 0){throw 'Owner offline checks failed.'}
    $trayCheck=Start-Process -FilePath (Join-Path $package 'bin/tray/ShieldController.exe') -ArgumentList '--selftest' -PassThru -Wait -WindowStyle Hidden
    try{if($trayCheck.ExitCode -ne 0){throw 'Tray offline checks failed.'}}finally{$trayCheck.Dispose()}
    $pairingBuild=Join-Path $debug ('pairing-build-'+[guid]::NewGuid().ToString('N'))
    & (Join-Path $repository 'pairing/build.ps1') -OutputDirectory $pairingBuild
    if($LASTEXITCODE -ne 0){throw 'Pairing worker build failed.'}
    New-Item -ItemType Directory -Path (Join-Path $package 'bin/pairing') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $pairingBuild 'wcn-pair-worker.exe') -Destination (Join-Path $package 'bin/pairing')
    foreach($name in @('Install-Dependencies.ps1','Uninstall.ps1')){
        $source=Join-Path $repository ('installer/'+$name)
        if(-not (Test-Path -LiteralPath $source)){throw ('Required installer helper missing: '+$name)}
        Copy-Item -LiteralPath $source -Destination (Join-Path $package 'bin')
    }
    Copy-Item -LiteralPath (Join-Path $repository 'LICENSE'),(Join-Path $repository 'THIRD-PARTY.md') -Destination (Join-Path $package 'licenses')
    & (Join-Path $repository 'scripts/collect-licenses.ps1') -OutputDirectory (Join-Path $package 'licenses')
    if($LASTEXITCODE -ne 0){throw 'Dependency license collection failed.'}
    foreach($symbol in @(Get-ChildItem -LiteralPath $package -Filter '*.pdb' -Recurse -File)){
        if(-not $symbol.FullName.StartsWith($package+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Symbol staging path escaped package.'}
        Remove-Item -LiteralPath $symbol.FullName
    }
    $files=[ordered]@{}
    Get-ChildItem -LiteralPath $package -Recurse -File | ForEach-Object { $relative=$_.FullName.Substring($package.Length+1).Replace('\','/');$files[$relative]=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    $manifest=[ordered]@{schema=1;product='NVIDIA-SHIELD-P2570-Windows';version=$Version;data_host_sha256=$files['bin/data-host/shield-controller-datahost.exe'];owner_exe_sha256=$files['bin/owner/ShieldControllerOwner.exe'];owner_dll_sha256=$files['bin/owner/ShieldControllerOwner.dll'];pairing_worker_sha256=$files['bin/pairing/wcn-pair-worker.exe'];tray_exe_sha256=$files['bin/tray/ShieldController.exe'];files=$files}
    [IO.File]::WriteAllText((Join-Path $package 'package.json'),($manifest|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
    & (Join-Path $package 'bin/data-host/shield-controller-datahost.exe')
    if($LASTEXITCODE -ne 0){throw 'Data host default plan failed.'}
    Write-Output $package
} finally {$env:TEMP=$previousTemp;$env:TMP=$previousTmp;$env:NPCAP_SDK_DIR=$previousSdk;$env:CARGO_ENCODED_RUSTFLAGS=$previousRustFlags}
