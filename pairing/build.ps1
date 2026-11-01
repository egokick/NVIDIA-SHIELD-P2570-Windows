param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
$debug = [IO.Path]::GetFullPath((Join-Path $repository 'debug')) + '\'
if (-not ($output + '\').StartsWith($debug, [StringComparison]::OrdinalIgnoreCase)) { throw 'Native build outputs must be under repository debug.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Build Tools with Desktop development with C++ is required.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $installation) { throw 'x64 MSVC toolchain unavailable.' }
$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$batch = Join-Path $output 'compile-native.cmd'
$source = Join-Path $PSScriptRoot 'worker.cpp'
$obj = Join-Path $output 'worker.obj'
$exe = Join-Path $output 'wcn-pair-worker.exe'
$commands = @('@echo off', ('call "{0}" >nul' -f $vcvars), 'if errorlevel 1 exit /b 1', ('cl.exe /nologo /std:c++17 /EHsc /W4 /WX /O2 /MT /DUNICODE /D_UNICODE /Fo"{0}" /Fe"{1}" "{2}" /link ole32.lib oleaut32.lib propsys.lib bcrypt.lib uuid.lib' -f $obj,$exe,$source), 'exit /b %errorlevel%')
[IO.File]::WriteAllLines($batch, $commands, [Text.Encoding]::ASCII)
& $env:ComSpec /d /c $batch
if ($LASTEXITCODE -ne 0) { throw 'Native pairing worker build failed.' }
& $exe --selftest
if ($LASTEXITCODE -ne 0) { throw 'Native pairing parser selftest failed.' }
& $exe --profile-selftest
if ($LASTEXITCODE -ne 0) { throw 'Native private profile comparison selftest failed.' }
