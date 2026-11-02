$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Install-Dependencies.ps1'
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $scriptPath -SelfTest
if ($LASTEXITCODE -ne 0) { throw 'Dependency planning checks failed.' }
# Load only the two file-guard function definitions. Never execute the helper's
# driver workflow, registry probes, downloads, or elevation in this fixture.
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'Dependency helper syntax errors.' }
foreach ($name in @('NoLinks','Allow-ElevatedDriverRead')) {
    $definition = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] },$false) | Where-Object Name -EQ $name
    if (@($definition).Count -ne 1) { throw 'Missing guard definition.' }
    Invoke-Expression $definition.Extent.Text
}
$debugRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\debug'))
$fixture = Join-Path $debugRoot ('dependency-acl-tests\'+[guid]::NewGuid().ToString('N'))
if (-not $fixture.StartsWith($debugRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $fixture)) { throw 'Invalid fixture.' }
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$file = Join-Path $fixture 'public-driver-fixture.txt'; [IO.File]::WriteAllText($file,'Known public driver placeholder. This is not executable.')
$parentBefore = (Get-Acl -LiteralPath $fixture).Sddl
NoLinks $file; Allow-ElevatedDriverRead $file
$acl = Get-Acl -LiteralPath $file; $rules = @($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $sid) { throw 'Public driver owner was not scoped to the installing user.' }
if (-not $acl.AreAccessRulesProtected -or $rules.Count -ne 3) { throw 'Public driver ACL was not scoped to three identities.' }
foreach ($rule in $rules) {
    if ($rule.AccessControlType -ne 'Allow') { throw 'Unexpected deny rule.' }
    $expected = if ($rule.IdentityReference.Value -eq $sid) { [Security.AccessControl.FileSystemRights]::FullControl } elseif ($rule.IdentityReference.Value -in @('S-1-5-32-544','S-1-5-18')) { [Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [Security.AccessControl.FileSystemRights]::Synchronize } else { throw 'Unexpected public driver principal.' }
    if ($rule.FileSystemRights -ne $expected) { throw 'Unexpected public driver permissions.' }
}
if ((Get-Acl -LiteralPath $fixture).Sddl -ne $parentBefore) { throw 'Parent directory permissions changed.' }
# Exercise the production ownership guard against foreign owners without
# assigning another account ownership of any real file. A rejected owner must
# fail before Set-Acl is reached, even for an elevated administrator.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $isAdministrator = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
finally { $identity.Dispose() }
$foreignOwners = @('S-1-5-21-99-98-97-1001','S-1-5-18')
if (-not $isAdministrator) { $foreignOwners += 'S-1-5-32-544' }
$ownershipChecks = 0
foreach ($owner in $foreignOwners) {
    $script:ownerProbeAcl = New-Object Security.AccessControl.FileSecurity
    $script:ownerProbeAcl.SetOwner([Security.Principal.SecurityIdentifier]::new($owner))
    $script:ownerProbeWrites = 0
    $beforeFileAcl = (Get-Acl -LiteralPath $file).Sddl
    function Get-Acl { param([string]$LiteralPath) return $script:ownerProbeAcl }
    function Set-Acl { param([string]$LiteralPath,$AclObject) $script:ownerProbeWrites++; throw 'Rejected owner reached ACL mutation.' }
    try {
        $rejected = $false
        try { Allow-ElevatedDriverRead $file }
        catch { if ($_.Exception.Message -eq 'The driver download belongs to a different Windows account.') { $rejected = $true } else { throw } }
        if (-not $rejected -or $script:ownerProbeWrites -ne 0) { throw 'Foreign driver owner was accepted or mutated.' }
        $ownershipChecks++
    } finally {
        Remove-Item -LiteralPath 'Function:\Get-Acl','Function:\Set-Acl'
        Remove-Variable -Name ownerProbeAcl,ownerProbeWrites -Scope Script
    }
    if ((Get-Acl -LiteralPath $file).Sddl -ne $beforeFileAcl) { throw 'Rejected ownership probe changed the real file.' }
}
$adminOwnedFixtureExercised = $false
if ($isAdministrator) {
    # Windows hosted runners create files owned by BUILTIN\Administrators.
    # Reproduce that ownership explicitly instead of relying on host defaults.
    $adminFile = Join-Path $fixture 'administrators-owned-driver-fixture.txt'
    [IO.File]::WriteAllText($adminFile,'Public elevated-installer ownership fixture. This is not executable.')
    $adminAcl = Get-Acl -LiteralPath $adminFile
    $adminAcl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    Set-Acl -LiteralPath $adminFile -AclObject $adminAcl
    if ((Get-Acl -LiteralPath $adminFile).GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544') { throw 'Elevated ownership fixture was not established.' }
    Allow-ElevatedDriverRead $adminFile
    $normalized = Get-Acl -LiteralPath $adminFile
    $normalizedRules = @($normalized.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
    if ($normalized.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $sid -or -not $normalized.AreAccessRulesProtected -or $normalizedRules.Count -ne 3) { throw 'Elevated driver ownership was not normalized to the installing user.' }
    foreach ($rule in $normalizedRules) {
        $expected = if ($rule.IdentityReference.Value -eq $sid) { [Security.AccessControl.FileSystemRights]::FullControl } elseif ($rule.IdentityReference.Value -in @('S-1-5-32-544','S-1-5-18')) { [Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [Security.AccessControl.FileSystemRights]::Synchronize } else { throw 'Unexpected elevated fixture principal.' }
        if ($rule.AccessControlType -ne 'Allow' -or $rule.FileSystemRights -ne $expected) { throw 'Elevated fixture broadened public-reader write access.' }
    }
    $adminOwnedFixtureExercised = $true; $ownershipChecks++
}
if ((Get-Acl -LiteralPath $fixture).Sddl -ne $parentBefore) { throw 'Ownership regression changed the parent directory permissions.' }
$handle = [IO.File]::Open($file,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try {
    $blocked = $false; try { [IO.File]::WriteAllText($file,'Replacement') } catch [IO.IOException] { $blocked = $true }
    if (-not $blocked) { throw 'Verified file could be modified during approval.' }
    $blocked = $false; try { [IO.File]::Move($file,$file+'.moved') } catch [IO.IOException] { $blocked = $true }
    if (-not $blocked) { throw 'Verified file could be replaced during approval.' }
} finally { $handle.Dispose() }
$mutex = [Threading.Mutex]::new($false,'Local\NVIDIA-SHIELD-P2570-DependencySetup')
$held = $false
try {
    try { $held = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
    if (-not $held) { throw 'An actual driver setup is running; concurrency test did not disturb it.' }
    $receiptName = 'dependency-result-'+[guid]::NewGuid().ToString('N')+'.json'
    $process = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$scriptPath+'"'),'-Component','ViGEmBus','-ResultFileName',$receiptName,'-DownloadOnly') -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $fixture 'busy.json') -RedirectStandardError (Join-Path $fixture 'busy.stderr')
    if ($process.ExitCode -ne 1618 -or (Get-Content -Raw -LiteralPath (Join-Path $fixture 'busy.json') | ConvertFrom-Json).phase -ne 'dependency-busy') { throw 'Concurrent helper did not fail before side effects.' }
    $receipt = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('NVIDIA-SHIELD-P2570-Windows\'+$receiptName)
    if (Test-Path -LiteralPath $receipt) { throw 'Rejected helper wrote a result file.' }
} finally { if ($held) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
@{passed=$true;acl_checks=6;ownership_checks=$ownershipChecks;admin_owned_fixture_exercised=$adminOwnedFixtureExercised;concurrency_checks=2;driver_changes=0;elevation_prompts=0;pairing_files_read=0} | ConvertTo-Json -Compress
