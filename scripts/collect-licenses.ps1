[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $output.StartsWith((Join-Path $repository 'debug')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Generated license bundle must be under debug/.'}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$metadataPath=Join-Path $repository 'debug/cargo-license-metadata.json'
& cargo metadata --locked --filter-platform x86_64-pc-windows-msvc --format-version 1 --manifest-path (Join-Path $repository 'Cargo.toml') | Set-Content -LiteralPath $metadataPath -Encoding UTF8
if($LASTEXITCODE -ne 0){throw 'Cargo license metadata failed.'}
$metadata=Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$text=[Text.StringBuilder]::new()
$null=$text.AppendLine('Rust dependency license texts for the locked Windows x64 build.')
foreach($package in @($metadata.packages | Sort-Object name,version)){
    if(-not $package.source){continue}
    $directory=Split-Path -Parent $package.manifest_path
    $licenseFiles=@(Get-ChildItem -LiteralPath $directory -File | Where-Object {$_.Name -match '\A(LICENSE|COPYING|COPYRIGHT|NOTICE)([._-]|$)'})
    if($licenseFiles.Count -eq 0 -and $package.name -ceq 'windows_x86_64_msvc' -and $package.version -ceq '0.36.1'){
        # This exact upstream import-library crate omitted its text files. Its
        # sibling windows-sys crate contains the licenses from the same commit.
        $sibling=@($metadata.packages | Where-Object {$_.name -ceq 'windows-sys' -and $_.version -ceq '0.36.1'})
        if($sibling.Count -ne 1){throw 'Matching Windows license source missing.'}
        $siblingDirectory=Split-Path -Parent $sibling[0].manifest_path
        foreach($sourceDirectory in @($directory,$siblingDirectory)){
            $vcs=Get-Content -LiteralPath (Join-Path $sourceDirectory '.cargo_vcs_info.json') -Raw | ConvertFrom-Json
            if($vcs.git.sha1 -cne '9deec0ee3b51a9a540f6dfe984ee34e2409ed355'){throw 'Windows license provenance differs.'}
        }
        $licenseFiles=@(Get-ChildItem -LiteralPath $siblingDirectory -File | Where-Object {$_.Name -in @('license-mit','license-apache')})
    }
    if($licenseFiles.Count -eq 0){throw ('No license text found for dependency '+$package.name)}
    $null=$text.AppendLine("`n===== $($package.name) $($package.version) =====")
    $null=$text.AppendLine('Declared license: '+$package.license)
    $null=$text.AppendLine('Source: '+$package.repository)
    foreach($file in $licenseFiles){$null=$text.AppendLine('--- '+$file.Name+' ---');$null=$text.AppendLine([IO.File]::ReadAllText($file.FullName))}
}
[IO.File]::WriteAllText((Join-Path $output 'RUST-DEPENDENCIES.txt'),$text.ToString(),[Text.UTF8Encoding]::new($false))
# Runtime framework packs are downloadDependencies, not normal assets.libraries.
# Include their original notices as well as normal NuGet package notices.
$dotnetText=[Text.StringBuilder]::new();$seen=@{}
foreach($assets in @(Get-ChildItem -LiteralPath (Join-Path $repository 'debug') -Filter project.assets.json -Recurse -File)){
    $asset=Get-Content -LiteralPath $assets.FullName -Raw | ConvertFrom-Json
    foreach($entry in $asset.libraries.PSObject.Properties){
        if($entry.Value.type -ne 'package' -or $seen.ContainsKey($entry.Name)){continue}
        foreach($folder in $asset.packageFolders.PSObject.Properties.Name){
            $path=Join-Path $folder $entry.Value.path
            if(-not (Test-Path -LiteralPath $path)){continue}
            $notices=@(Get-ChildItem -LiteralPath $path -File | Where-Object {$_.Name -match '(LICENSE|NOTICE|COPYRIGHT)'})
            if($notices.Count -eq 0){continue}
            $seen[$entry.Name]=$true;$null=$dotnetText.AppendLine("`n===== $($entry.Name) =====")
            foreach($notice in $notices){$null=$dotnetText.AppendLine('--- '+$notice.Name+' ---');$null=$dotnetText.AppendLine([IO.File]::ReadAllText($notice.FullName))}
            break
        }
    }
}
foreach($idDirectory in @(Get-ChildItem -LiteralPath (Join-Path $repository 'debug/dotnet-packages') -Directory)){
    foreach($versionDirectory in @(Get-ChildItem -LiteralPath $idDirectory.FullName -Directory)){
        $name=$idDirectory.Name+'/'+$versionDirectory.Name
        if($seen.ContainsKey($name)){continue}
        $notices=@(Get-ChildItem -LiteralPath $versionDirectory.FullName -File | Where-Object {$_.Name -match '(LICENSE|NOTICE|COPYRIGHT)'})
        if($notices.Count -eq 0){continue}
        $seen[$name]=$true;$null=$dotnetText.AppendLine("`n===== $name =====")
        foreach($notice in $notices){$null=$dotnetText.AppendLine('--- '+$notice.Name+' ---');$null=$dotnetText.AppendLine([IO.File]::ReadAllText($notice.FullName))}
    }
}
if($seen.Count -eq 0){throw 'No .NET package license notices found after publish.'}
$sdkLicense=Join-Path $repository 'debug/sdk/windows-sdk-license.rtf'
New-Item -ItemType Directory -Path (Split-Path -Parent $sdkLicense) -Force | Out-Null
if(-not (Test-Path -LiteralPath $sdkLicense)){Invoke-WebRequest -UseBasicParsing -Uri 'https://download.microsoft.com/download/0/F/F/0FF2B061-47DD-4F55-89B6-FD1D8C44F14D/sdk_license.rtf' -OutFile $sdkLicense}
if((Get-FileHash -LiteralPath $sdkLicense -Algorithm SHA256).Hash -ne 'DD07EB178E00C6BBA4148457FC00FF77CD4887EB521D504186FE59C9EC8BBE62'){throw 'Windows SDK license download changed.'}
Copy-Item -LiteralPath $sdkLicense -Destination (Join-Path $output 'WINDOWS-SDK-LICENSE.rtf')
$null=$dotnetText.AppendLine("`nMicrosoft.Windows.SDK.NET.Ref: Microsoft Windows SDK license is included as WINDOWS-SDK-LICENSE.rtf. The official redistributable list permits the unmodified package files used to call WinRT APIs: https://learn.microsoft.com/en-us/legal/windows-sdk/redist#microsoftwindowssdknetref")
[IO.File]::WriteAllText((Join-Path $output 'DOTNET-DEPENDENCIES.txt'),$dotnetText.ToString(),[Text.UTF8Encoding]::new($false))
Write-Output ('License bundle generated; .NET packages with included notices: '+$seen.Count)
