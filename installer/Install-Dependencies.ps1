[CmdletBinding()]
param(
    [ValidateSet('All','Npcap','ViGEmBus')][string]$Component = 'All',
    [ValidatePattern('\Adependency-result-[a-f0-9]{32}\.json\z')][string]$ResultFileName = '',
    [switch]$DownloadOnly,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-InstallerArguments([string]$Name,[hashtable]$NpcapFeatures = @{}) {
    if ($Name -eq 'Npcap') {
        # The free edition must show its own licence. These documented GUI
        # options prevent the common admin-only mistake without using /S.
        $arguments = @('/admin_only=disabled','/no_kill=yes')
        foreach ($option in @('winpcap_mode','dot11_support','loopback_support')) {
            $value = if ($NpcapFeatures[$option] -eq 1) { 'yes' } else { 'no' }
            $arguments += ('/'+$option+'='+$value)
        }
        return $arguments
    }
    if ($Name -eq 'ViGEmBus') { return @('/exenoui','/qn','/norestart') }
    throw 'Unknown driver.'
}
function Get-DriverPlan([bool]$NpcapReady,[bool]$ViGEmReady,[string]$Selection) {
    if ($Selection -in @('All','Npcap') -and -not $NpcapReady) { 'Npcap' }
    if ($Selection -in @('All','ViGEmBus') -and -not $ViGEmReady) { 'ViGEmBus' }
}
function Test-DriverReady([string]$Name) {
    if ($Name -eq 'Npcap') {
        $service = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\npcap' -ErrorAction SilentlyContinue
        $settings = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\npcap\Parameters' -ErrorAction SilentlyContinue
        $nonAdmin = ($null -eq $settings -or $null -eq $settings.AdminOnly -or ($settings.AdminOnly -is [int] -and $settings.AdminOnly -eq 0))
        return ($null -ne $service -and $nonAdmin -and (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('System')) 'Npcap\wpcap.dll')))
    }
    return (Test-Path -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\ViGEmBus')
}
function Test-InstallerResult([string]$Name,[int]$Code,[bool]$Ready) {
    if ($Code -eq 350) { return 'restart-retry' }
    if ($Code -in @(3010,1641)) { return 'restart' }
    if ($Ready -and ($Code -eq 0 -or ($Name -eq 'Npcap' -and $Code -eq 2))) { return 'ready' }
    if ($Code -in @(1,1602,1223)) { throw 'Setup was cancelled. Click Try again when you are ready.' }
    if ($Code -eq 1618) { throw 'Another installer is running. Finish it, then click Try again.' }
    if ($Code -eq 0) { throw 'Windows has not enabled the driver yet. Restart Windows, then open SHIELD Controller.' }
    throw ('The driver could not finish (code ' + $Code + '). Click Try again, or restart Windows first.')
}
if ($SelfTest) {
    $checks = 0
    function Check([bool]$Value) { if (-not $Value) { throw 'Dependency flow check failed.' }; $script:checks++ }
    Check ((@(Get-DriverPlan $false $false 'All') -join ',') -eq 'Npcap,ViGEmBus')
    Check ((@(Get-DriverPlan $true $false 'All') -join ',') -eq 'ViGEmBus')
    Check (@(Get-DriverPlan $true $true 'All').Count -eq 0)
    Check ((@(Get-DriverPlan $false $false 'ViGEmBus') -join ',') -eq 'ViGEmBus')
    Check (@(Get-InstallerArguments 'Npcap') -contains '/admin_only=disabled')
    Check (@(Get-InstallerArguments 'Npcap') -notcontains '/S')
    Check (@(Get-InstallerArguments 'Npcap' @{dot11_support=1;winpcap_mode=1}) -contains '/dot11_support=yes')
    Check (@(Get-InstallerArguments 'Npcap' @{dot11_support=1;winpcap_mode=1}) -contains '/winpcap_mode=yes')
    Check ((@(Get-InstallerArguments 'ViGEmBus') -join ' ') -eq '/exenoui /qn /norestart')
    Check ((Test-InstallerResult 'Npcap' 2 $true) -eq 'ready')
    Check ((Test-InstallerResult 'ViGEmBus' 0 $true) -eq 'ready')
    Check ((Test-InstallerResult 'Npcap' 3010 $false) -eq 'restart')
    Check ((Test-InstallerResult 'Npcap' 350 $false) -eq 'restart-retry')
    foreach ($code in @(0,1,1602,1223,1618,5)) {
        $failed = $false; try { $null = Test-InstallerResult 'Npcap' $code $false } catch { $failed = $true }; Check $failed
    }
    @{passed=$true;checks=$checks;downloads=0;driver_changes=0} | ConvertTo-Json -Compress
    exit 0
}
if (-not $ResultFileName) { [Console]::Error.WriteLine('A unique dependency result filename is required.'); exit 2 }

$stateRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'NVIDIA-SHIELD-P2570-Windows'
$downloads = Join-Path $stateRoot 'downloads'
$resultPath = Join-Path $stateRoot $ResultFileName
$catalog = @{
    Npcap = @{ Url='https://npcap.com/dist/npcap-1.90.exe'; File='npcap-1.90.exe'; Hash='682B57E1C05EF375931975C9D6DED40888F4BFDF9F4B11571E654F962F1199B6'; Publisher='CN=Nmap Software LLC,' }
    ViGEmBus = @{ File='ViGEmBus_1.22.0_x64_x86_arm64.exe'; Hash='89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A'; Publisher='CN=Nefarius Software Solutions e.U.,' }
}
function Progress([string]$Name,[string]$Step) { @{phase='dependency-progress';component=$Name;step=$Step} | ConvertTo-Json -Compress | Write-Output }
function NoLinks([string]$Path) {
    for ($current=[IO.Path]::GetFullPath($Path); $current; $current=[IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -Force -LiteralPath $current).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The installation folder cannot be a link.' }
    }
}
function Allow-ElevatedDriverRead([string]$Path) {
    # These are public, signed installers only. Pairing files and directories
    # retain their private ACL. A different administrator entered at UAC must
    # be able to read the executable, but never receives write permission here.
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $sid = $identity.User
    $acl = Get-Acl -LiteralPath $Path
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier])
    # Elevated Windows processes can create files owned by Administrators.
    # Accept that owner only for an already elevated administrator, then use
    # the same explicit per-user ownership as an ordinary desktop install.
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    $adminOwned = $owner.Value -eq 'S-1-5-32-544' -and $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $sid.Equals($owner) -and -not $adminOwned) { throw 'The driver download belongs to a different Windows account.' }
    $fileAcl = New-Object Security.AccessControl.FileSecurity
    $fileAcl.SetOwner($sid)
    $fileAcl.SetAccessRuleProtection($true,$false)
    $fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','Allow'))
    foreach ($publicReader in @('S-1-5-32-544','S-1-5-18')) {
        $fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($publicReader),'ReadAndExecute','Allow'))
    }
    Set-Acl -LiteralPath $Path -AclObject $fileAcl
}
$dependencyMutex = [Threading.Mutex]::new($false,'Local\NVIDIA-SHIELD-P2570-DependencySetup')
$dependencyHeld = $false
try { $dependencyHeld = $dependencyMutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $dependencyHeld = $true }
if (-not $dependencyHeld) {
    # Do not replace an active operation's receipt on a lock failure.
    @{phase='dependency-busy';message='Another SHIELD driver setup is running. Finish that window, then try again.'} | ConvertTo-Json -Compress | Write-Output
    $dependencyMutex.Dispose(); exit 1618
}
try {
    foreach ($directory in @($stateRoot,$downloads)) { NoLinks $directory; if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null } }
    NoLinks $resultPath
    $restart = $false; $restartRetry = $false
    $plan = @(Get-DriverPlan (Test-DriverReady 'Npcap') (Test-DriverReady 'ViGEmBus') $Component)
    if ($DownloadOnly) { $plan = @($(if ($Component -eq 'All') { 'Npcap'; 'ViGEmBus' } else { $Component })) }
    foreach ($name in $plan) {
        $entry = $catalog[$name]
        if ($name -eq 'ViGEmBus') {
            # BSD-licensed, signed original is bundled by build-installer.ps1.
            $download = Join-Path $PSScriptRoot ('drivers\' + $entry.File)
            if (-not (Test-Path -LiteralPath $download -PathType Leaf)) { throw 'The bundled gamepad driver is missing. Download and run SHIELD Setup again.' }
        } else {
            $download = Join-Path $downloads $entry.File
            NoLinks $download
            if (-not (Test-Path -LiteralPath $download) -or (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $entry.Hash) {
                Progress $name 'download'
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                $temporary = $download + '.' + [guid]::NewGuid().ToString('N') + '.part'
                try {
                    Invoke-WebRequest -UseBasicParsing -Uri $entry.Url -OutFile $temporary -TimeoutSec 120
                    if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $entry.Hash) { throw 'The connection tool download could not be verified. Nothing was run. Click Try again.' }
                    Move-Item -LiteralPath $temporary -Destination $download -Force
                } catch [System.Net.WebException] { throw 'The connection tool could not download. Check your internet connection, then click Try again.' }
                finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
            }
        }
        NoLinks $download
        Allow-ElevatedDriverRead $download
        # Keep the verified bytes fixed while Windows waits for UAC and while
        # the original vendor's installer process tree is running.
        $verifiedFile = [IO.File]::Open($download,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        try {
        Progress $name 'verify'
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $entry.Hash) { throw 'A driver failed its safety check. Download and run SHIELD Setup again. Nothing was run.' }
        $signature = Get-AuthenticodeSignature -LiteralPath $download
        if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate.Subject.StartsWith($entry.Publisher,[StringComparison]::Ordinal)) { throw 'Windows could not verify the official driver signature. Check the PC clock and internet connection, then try again. Nothing was run.' }
        if ($DownloadOnly) { continue }
        Progress $name 'install'
        try {
            # -Wait follows the installer process tree, including Npcap's
            # background child. WaitForExit on just its launcher is insufficient.
            $windowStyle = if ($name -eq 'Npcap') { 'Normal' } else { 'Hidden' }
            $features = @{}
            if ($name -eq 'Npcap') {
                # Preserve optional capture features used by other apps when
                # repairing an admin-only installation. Fresh installs need none.
                $existing = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\npcap\Parameters' -ErrorAction SilentlyContinue
                foreach ($pair in @(@('winpcap_mode','WinPcapCompatible'),@('dot11_support','Dot11Support'),@('loopback_support','LoopbackSupport'))) {
                    if ($existing -and $existing.($pair[1]) -is [int]) { $features[$pair[0]] = $existing.($pair[1]) }
                }
            }
            $process = Start-Process -FilePath $download -ArgumentList @(Get-InstallerArguments $name $features) -Verb RunAs -WindowStyle $windowStyle -Wait -PassThru
            $exitCode = $process.ExitCode
            if ($null -eq $exitCode) { throw 'Windows did not return a driver setup result. Restart Windows and open SHIELD Controller.' }
        } catch [System.ComponentModel.Win32Exception] {
            if ($_.Exception.NativeErrorCode -eq 1223) { throw 'Windows approval was cancelled. Click Try again, then click Yes in the Windows prompt.' }
            throw
        }
        $outcome = Test-InstallerResult $name $exitCode (Test-DriverReady $name)
        if ($outcome -eq 'restart-retry') { $restart = $true; $restartRetry = $true; Progress $name $outcome; break }
        if ($outcome -eq 'restart') { $restart = $true }
        Progress $name $outcome
        } finally { $verifiedFile.Dispose() }
    }
    @{component=$Component;ok=$true;download_only=[bool]$DownloadOnly;restart_required=$restart;restart_retry_required=$restartRetry;message=$(if ($restartRetry) {'Restart Windows, then run SHIELD Setup again to finish the connection tool.'} elseif ($restart) {'Restart Windows, then open SHIELD Controller.'} elseif ($DownloadOnly) {'Official drivers verified.'} else {'Drivers are ready.'})} | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    if ($restartRetry) { exit 350 }; if ($restart) { exit 3010 }; exit 0
} catch {
    $failure = $_.Exception.Message
    try { NoLinks $resultPath; if (Test-Path -LiteralPath $stateRoot) { @{component=$Component;ok=$false;restart_required=$false;message=$failure} | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8 } } catch { }
    exit 1
} finally {
    if ($dependencyHeld) { $dependencyMutex.ReleaseMutex() }; $dependencyMutex.Dispose()
}
