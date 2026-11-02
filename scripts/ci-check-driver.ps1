[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# This changes only a disposable hosted runner. Never invoke on a user's PC.
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'This driver integration check is only for GitHub Actions.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'The disposable runner must already be elevated.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$evidence = Join-Path $repoRoot 'debug/driver-integration'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$helper = Join-Path $repoRoot 'debug/package/bin/Install-Dependencies.ps1'
$powershell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
# Exercise the actual official download and verification without accepting or
# installing Npcap's interactive licence on a headless runner.
$resultName = 'dependency-result-' + [guid]::NewGuid().ToString('N') + '.json'
& $powershell -NoProfile -ExecutionPolicy Bypass -File $helper -Component Npcap -DownloadOnly -ResultFileName $resultName
if ($LASTEXITCODE -ne 0) { throw 'Official Npcap download verification failed.' }
$driverExisted = Test-Path -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\ViGEmBus'
$resultName = 'dependency-result-' + [guid]::NewGuid().ToString('N') + '.json'
& $powershell -NoProfile -ExecutionPolicy Bypass -File $helper -Component ViGEmBus -ResultFileName $resultName
$installExit = $LASTEXITCODE
if ($installExit -notin @(0,3010)) { throw ('Bundled gamepad driver installation failed: '+$installExit) }
$output = & (Join-Path $repoRoot 'debug/package/bin/data-host/shield-controller-datahost.exe') --check-drivers
if ($LASTEXITCODE -ne 0) { throw 'Native driver probe failed.' }
$probe = $output | ConvertFrom-Json
if ($probe.starts_or_stops_ap -or $probe.virtual_pad_created) { throw 'Driver probe must not start a controller or hotspot.' }
if ($installExit -eq 0 -and -not $probe.vigem) { throw 'The installed gamepad driver could not be opened.' }
[ordered]@{installer_exit=$installExit;driver_previously_installed=$driverExisted;gamepad_driver_opened=[bool]$probe.vigem;restart_required=($installExit -eq 3010);npcap_download_verified=$true;controller_radio_tested=$false} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'result.json')
Get-Content -LiteralPath (Join-Path $evidence 'result.json')
