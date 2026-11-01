[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Npcap','ViGEmBus')][string]$Component,
    [switch]$DownloadOnly
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$stateRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'NVIDIA-SHIELD-P2570-Windows'
$downloads = Join-Path $stateRoot 'downloads'
$resultPath = Join-Path $stateRoot 'dependency-result.json'
$catalog = @{
    Npcap = @{ Url='https://npcap.com/dist/npcap-1.90.exe'; File='npcap-1.90.exe'; Hash='682B57E1C05EF375931975C9D6DED40888F4BFDF9F4B11571E654F962F1199B6'; Publisher='CN=Nmap Software LLC,' }
    ViGEmBus = @{ Url='https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe'; File='ViGEmBus_1.22.0_x64_x86_arm64.exe'; Hash='89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A'; Publisher='CN=Nefarius Software Solutions e.U.,' }
}
try {
    foreach ($directory in @($stateRoot,$downloads)) {
        if (Test-Path -LiteralPath $directory) {
            if ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The installation folder cannot be a link.' }
        } else { New-Item -ItemType Directory -Path $directory | Out-Null }
    }
    $entry = $catalog[$Component]
    $download = Join-Path $downloads $entry.File
    if (Test-Path -LiteralPath $download) {
        if ((Get-Item -LiteralPath $download).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The download file cannot be a link.' }
    }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    if (-not (Test-Path -LiteralPath $download) -or (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $entry.Hash) {
        Invoke-WebRequest -UseBasicParsing -Uri $entry.Url -OutFile $download
    }
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $entry.Hash) { throw 'The downloaded installer failed its integrity check. Nothing was run.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $download
    if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate.Subject.StartsWith($entry.Publisher,[StringComparison]::Ordinal)) { throw 'The official installer signature could not be verified. Nothing was run.' }
    $exitCode = 0
    if (-not $DownloadOnly) {
        # Npcap free requires its own interactive licence and setup prompts.
        # Neither dependency is redistributed or installed with a silent switch.
        $process = Start-Process -FilePath $download -Verb RunAs -PassThru
        $null = $process.Handle
        $process.WaitForExit()
        $process.WaitForExit()
        $exitCode = $process.ExitCode
        if ($null -eq $exitCode) { throw 'The official installer did not return a result. Check its window and retry the requirements check.' }
        if ($exitCode -notin @(0,3010)) { throw "The official installer returned exit code $exitCode. Installation was not confirmed." }
    }
    @{component=$Component;ok=$true;download_only=[bool]$DownloadOnly;restart_required=($exitCode -eq 3010);message= $(if ($DownloadOnly) {'Official download verified.'} elseif ($exitCode -eq 3010) {'Restart Windows to finish driver installation.'} else {'Installer finished. Refresh requirements to check the driver.'})} | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    exit $exitCode
} catch {
    if (Test-Path -LiteralPath $stateRoot) { @{component=$Component;ok=$false;message=$_.Exception.Message} | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8 }
    exit 1
}
