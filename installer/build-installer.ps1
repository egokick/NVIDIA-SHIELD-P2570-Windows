[CmdletBinding()]
param([string]$PackagePath = '')
$ErrorActionPreference = 'Stop'
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test-uninstall-paths.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Uninstaller path checks failed' }
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test-dependencies.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Dependency flow checks failed' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $PackagePath) { $PackagePath = Join-Path $repoRoot 'debug\package' }
$stage = [IO.Path]::GetFullPath($PackagePath)
$debugRoot = Join-Path $repoRoot 'debug'
if (-not $stage.StartsWith($debugRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Package output must be under debug.' }
foreach ($required in @('package.json','bin\tray\ShieldController.exe','bin\owner\ShieldControllerOwner.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $required) -PathType Leaf)) { throw "Package is missing $required" }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-Dependencies.ps1'),(Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination (Join-Path $stage 'bin') -Force
# ViGEmBus is BSD-3-Clause and redistributable. Keep the original signed
# installer unchanged. Npcap free is NOT redistributable and is never bundled.
$driverCache = Join-Path $debugRoot 'official-driver-cache'
$driverFiles = Join-Path $stage 'bin\drivers'
New-Item -ItemType Directory -Force -Path $driverCache,$driverFiles | Out-Null
$driverName = 'ViGEmBus_1.22.0_x64_x86_arm64.exe'
$driver = Join-Path $driverCache $driverName
$driverHash = '89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A'
if (-not (Test-Path -LiteralPath $driver) -or (Get-FileHash -LiteralPath $driver -Algorithm SHA256).Hash -ne $driverHash) {
    Invoke-WebRequest -UseBasicParsing -Uri ('https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/'+$driverName) -OutFile $driver
}
if ((Get-FileHash -LiteralPath $driver -Algorithm SHA256).Hash -ne $driverHash) { throw 'Official ViGEmBus download differs from the pinned release.' }
$driverSignature = Get-AuthenticodeSignature -LiteralPath $driver
if ($driverSignature.Status -ne 'Valid' -or -not $driverSignature.SignerCertificate.Subject.StartsWith('CN=Nefarius Software Solutions e.U.,',[StringComparison]::Ordinal)) { throw 'Official ViGEmBus signature is invalid.' }
Copy-Item -LiteralPath $driver -Destination $driverFiles -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses\ViGEmBus.txt') -Destination (Join-Path $stage 'licenses\ViGEmBus.txt') -Force
foreach ($file in @('README.md','LICENSE','THIRD-PARTY.md')) { Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination (Join-Path $stage $file) -Force }
$manifestFile = Join-Path $stage 'package.json'
$manifest = Get-Content -Raw -LiteralPath $manifestFile | ConvertFrom-Json
$files = [ordered]@{}
Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object FullName -NE $manifestFile | ForEach-Object {
    $relative = $_.FullName.Substring($stage.Length + 1).Replace('\','/')
    $files[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}
$manifest.files = $files
[IO.File]::WriteAllText($manifestFile,($manifest | ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
$release = Join-Path $debugRoot 'release'
New-Item -ItemType Directory -Force -Path $release | Out-Null
$archive = Join-Path $release 'payload.zip'
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in @(Get-ChildItem -LiteralPath $stage -Recurse -File)) {
        $relative = $file.FullName.Substring($stage.Length + 1).Replace('\','/')
        $entry = $zip.CreateEntry($relative,[IO.Compression.CompressionLevel]::Optimal)
        $input = [IO.File]::OpenRead($file.FullName)
        try { $output = $entry.Open(); try { $input.CopyTo($output) } finally { $output.Dispose() } }
        finally { $input.Dispose() }
    }
} finally { $zip.Dispose() }
# Inspect the stored names without a reader that normalizes Windows separators.
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $names = @($zip.Entries | ForEach-Object { $_.FullName })
    if ($names.Count -ne ($files.Count + 1) -or @($names | Where-Object { $_.Contains('\') }).Count -ne 0) { throw 'Archive names are not canonical forward-slash paths.' }
    foreach ($name in $names) { if ($name -ne 'package.json' -and -not $files.Contains($name)) { throw 'Archive contains an unexpected file.' } }
} finally { $zip.Dispose() }
$out = Join-Path $debugRoot 'installer-publish'
dotnet publish (Join-Path $PSScriptRoot 'ShieldController.Setup.csproj') -c Release -r win-x64 --self-contained true -o $out "-p:PayloadPath=$archive" "-p:BaseIntermediateOutputPath=$debugRoot\installer-obj\" "-p:BaseOutputPath=$debugRoot\installer-bin\"
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
if ($manifest.version -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') { throw 'Invalid package version.' }
$setup = Join-Path $release ('NVIDIA-SHIELD-P2570-Windows-Setup-' + $manifest.version + '.exe')
Copy-Item -LiteralPath (Join-Path $out 'ShieldControllerSetup.exe') -Destination $setup -Force
$friendlySetup = Join-Path $release 'SHIELD-Controller-Setup.exe'
Copy-Item -LiteralPath $setup -Destination $friendlySetup -Force
$fixture = Join-Path $debugRoot ('installer-selftest\' + [guid]::NewGuid().ToString('N'))
$checkOutput = Join-Path $debugRoot ('installer-selftest-' + [IO.Path]::GetFileName($fixture) + '.jsonl')
$check = Start-Process -FilePath $setup -ArgumentList @('--selftest','--fixture-root',('"' + $fixture + '"')) -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput $checkOutput -RedirectStandardError ($checkOutput + '.stderr')
if ($check.ExitCode -ne 0) { Get-Content -LiteralPath ($checkOutput + '.stderr'); throw 'Installer path validation failed' }
Get-Content -LiteralPath $checkOutput
$flowOutput = Join-Path $debugRoot ('installer-flow-selftest-' + [IO.Path]::GetFileName($fixture) + '.jsonl')
$flowCheck = Start-Process -FilePath $setup -ArgumentList '--flow-selftest' -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput $flowOutput -RedirectStandardError ($flowOutput + '.stderr')
if ($flowCheck.ExitCode -ne 0) { Get-Content -LiteralPath ($flowOutput + '.stderr'); throw 'Guided installer flow checks failed' }
Get-Content -LiteralPath $flowOutput
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
if ((Get-FileHash -LiteralPath $friendlySetup -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'Friendly download differs from the versioned installer.' }
@("$hash  $([IO.Path]::GetFileName($setup))", "$hash  $([IO.Path]::GetFileName($friendlySetup))") | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding ASCII
Write-Output $setup
