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
@{passed=$true;acl_checks=6;concurrency_checks=2;driver_changes=0;elevation_prompts=0;pairing_files_read=0} | ConvertTo-Json -Compress
