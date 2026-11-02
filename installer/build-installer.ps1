[CmdletBinding()]
param([string]$PackagePath = '')
$ErrorActionPreference = 'Stop'
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test-uninstall-paths.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Uninstaller path checks failed' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $PackagePath) { $PackagePath = Join-Path $repoRoot 'debug\package' }
$stage = [IO.Path]::GetFullPath($PackagePath)
$debugRoot = Join-Path $repoRoot 'debug'
if (-not $stage.StartsWith($debugRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Package output must be under debug.' }
foreach ($required in @('package.json','bin\tray\ShieldController.exe','bin\owner\ShieldControllerOwner.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $required) -PathType Leaf)) { throw "Package is missing $required" }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-Dependencies.ps1'),(Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination (Join-Path $stage 'bin') -Force
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
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$archive,[IO.Compression.CompressionLevel]::Optimal,$false)
$out = Join-Path $debugRoot 'installer-publish'
dotnet publish (Join-Path $PSScriptRoot 'ShieldController.Setup.csproj') -c Release -r win-x64 --self-contained true -o $out "-p:PayloadPath=$archive" "-p:BaseIntermediateOutputPath=$debugRoot\installer-obj\" "-p:BaseOutputPath=$debugRoot\installer-bin\"
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
$setup = Join-Path $release 'NVIDIA-SHIELD-P2570-Windows-Setup-0.1.0.exe'
Copy-Item -LiteralPath (Join-Path $out 'ShieldControllerSetup.exe') -Destination $setup -Force
$check = Start-Process -FilePath $setup -ArgumentList '--selftest' -WindowStyle Hidden -PassThru -Wait
if ($check.ExitCode -ne 0) { throw 'Installer path validation failed' }
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($setup))" | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding ASCII
Write-Output $setup
