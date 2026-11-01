[CmdletBinding()]
param([switch]$Quiet)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$product = 'NVIDIA SHIELD P2570 for Windows'
$installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'NVIDIA-SHIELD-P2570-Windows'
function Assert-NormalPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The installation path contains a link. No files were removed.' }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
function Test-PackageRelativePath([string]$Relative) {
    if (-not $Relative -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')) { return $false }
    $normalized = $Relative.Replace('\','/')
    foreach ($part in $normalized.Split('/')) {
        if (-not $part -or $part -in @('.','..') -or $part.TrimEnd([char[]]' .') -cne $part -or $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $part -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)') { return $false }
    }
    return $normalized.StartsWith('bin/',[StringComparison]::Ordinal) -or $normalized.StartsWith('licenses/',[StringComparison]::Ordinal) -or (@('package.json','README.md','LICENSE','THIRD-PARTY.md') -ccontains $normalized)
}
function Read-PackageFileList([string]$Json) {
    $decoded = $Json | ConvertFrom-Json
    if ($decoded -isnot [Array]) { throw 'Invalid installation manifest. No files were removed.' }
    foreach ($entry in $decoded) {
        if ($entry -isnot [string] -or -not (Test-PackageRelativePath $entry)) { throw 'Invalid installation manifest. No files were removed.' }
        Write-Output $entry
    }
}
try {
    Assert-NormalPath $installRoot
    if (-not $Quiet -and [Windows.Forms.MessageBox]::Show('Remove the controller app? Pairing details are kept for reinstallation. Shared Npcap and ViGEmBus drivers are kept.',$product,'OKCancel','Question') -ne 'OK') { exit 0 }
    $tray = Join-Path $installRoot 'bin\tray\ShieldController.exe'
    if (Test-Path -LiteralPath $tray) {
        $stop = Start-Process -FilePath $tray -ArgumentList '--exit' -WindowStyle Hidden -PassThru
        if (-not $stop.WaitForExit(30000)) { throw 'The app is still shutting down. Exit the tray app and try again.' }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $active = @()
        foreach ($process in @(Get-Process -Name 'ShieldController','ShieldControllerOwner','shield-controller-datahost','wcn-pair-worker' -ErrorAction SilentlyContinue)) {
            if ($process.HasExited) { continue }
            $imagePath = $process.Path
            if (-not $imagePath) { throw 'Cannot verify that a controller process has exited. Close the controller app and retry.' }
            if ($imagePath.StartsWith((Join-Path $installRoot 'bin') + '\',[StringComparison]::OrdinalIgnoreCase)) { $active += $process }
        }
        if ($active.Count -eq 0) { break }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'The controller connection has not closed yet. Disconnect and exit the app, then retry.' }
        Start-Sleep -Milliseconds 200
    } while ($true)
    if ((Test-Path -LiteralPath (Join-Path $installRoot 'recovery.json')) -or (Test-Path -LiteralPath (Join-Path $installRoot 'setup\active.json'))) { throw 'The controller session needs recovery. Open the app and use Recover before uninstalling.' }
    $manifestPath = Join-Path $installRoot 'installed-files.json'
    Assert-NormalPath $manifestPath
    $files = @(Read-PackageFileList (Get-Content -Raw -LiteralPath $manifestPath))
    $targets = @()
    foreach ($relative in $files) {
        if ($relative -isnot [string] -or -not (Test-PackageRelativePath $relative)) { throw 'Invalid installation manifest. No files were removed.' }
        $target = [IO.Path]::GetFullPath((Join-Path $installRoot $relative))
        if (-not $target.StartsWith($installRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'File is outside the installation folder.' }
        Assert-NormalPath $target
        if (Test-Path -LiteralPath $target -PathType Container) { throw 'Manifest file unexpectedly names a folder. No files were removed.' }
        $targets += $target
    }
    # All resolved paths are checked before removing any exact owned files.
    foreach ($target in $targets) { if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force } }
    Remove-Item -LiteralPath $manifestPath -Force
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) ($product + '.lnk')
    if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut }
    $registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NVIDIA-SHIELD-P2570-Windows'
    if (Test-Path -LiteralPath $registry) { Remove-Item -LiteralPath $registry }
    if (-not $Quiet) { [Windows.Forms.MessageBox]::Show('The app was removed. Your pairing details and shared drivers were kept.',$product,'OK','Information') | Out-Null }
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    if (-not $Quiet) { [Windows.Forms.MessageBox]::Show($_.Exception.Message,$product,'OK','Error') | Out-Null }
    exit 1
}
