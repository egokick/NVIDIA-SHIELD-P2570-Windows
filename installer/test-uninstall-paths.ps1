$ErrorActionPreference = 'Stop'
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Uninstall.ps1'),[ref]$null,[ref]$errors)
if ($errors.Count) { throw $errors }
$function = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-PackageRelativePath'},$false)
if (-not $function) { throw 'Package path validator missing' }
# Load only the pure validator; never execute uninstall's entry point in a test.
. ([ScriptBlock]::Create($function.Extent.Text))
$reader = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Read-PackageFileList'},$false)
if (-not $reader) { throw 'Manifest reader missing' }
. ([ScriptBlock]::Create($reader.Extent.Text))
$checks = 0
foreach ($path in @('bin\tray\ShieldController.exe','licenses/GPL-2.0.txt','package.json','README.md')) {
    if (-not (Test-PackageRelativePath $path)) { throw "Valid package path rejected: $path" }; $checks++
}
foreach ($path in @('..\outside','bin\..\outside','bin\.. \.. \outside.txt','bin/file.','bin/file ','bin//file','bin/./file','bin/CON.txt','bin/LPT1','bin/a:stream','bin/a?.dll','config.json','C:\outside','/bin/file')) {
    if (Test-PackageRelativePath $path) { throw 'Unsafe package path was accepted' }; $checks++
}
$decoded = @(Read-PackageFileList '["bin/tray/ShieldController.exe","LICENSE"]')
if ($decoded.Count -ne 2 -or $decoded[0] -isnot [string]) { throw 'Manifest array decoding failed' }; $checks++
foreach ($json in @('"LICENSE"','{"path":"LICENSE"}','["config.json"]','[1]')) {
    $rejected = $false
    try { Read-PackageFileList $json | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid manifest accepted' }; $checks++
}
@{passed=$true;checks=$checks;files_deleted=0;hardware_calls=0} | ConvertTo-Json -Compress
exit 0
