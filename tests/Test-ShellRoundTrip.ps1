#Requires -Version 7.0
param([switch]$ReadOnly)
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use pwsh -STA.' }
$root = Split-Path $PSScriptRoot -Parent
Add-Type -Path (Join-Path $root 'native\ShellDesktop.cs') -CompilerOptions '/nowarn:9191'
$desktop = [ShellDesktop]::new()
try {
    $before = $desktop.Capture()
    $before | Select-Object viewMode,iconSize,folderFlags,@{n='itemCount';e={$_.items.Count}} | Format-List
    if ($ReadOnly) { return }
    # A recoverable snapshot of the actual desktop, not the user's saved profiles.
    $logDir = Join-Path $root 'test-logs'
    [IO.Directory]::CreateDirectory($logDir) | Out-Null
    $backup = Join-Path $logDir ('desktop-before-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
    $before | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $backup -Encoding utf8
    Write-Output "Baseline: $backup"
    $restored = $false
    try {
        foreach ($delta in @(-4, 4)) {
            $size = [Math]::Clamp($before.iconSize + $delta, 16, 256)
            $desktop.ApplySize($before.viewMode, $size)
            $changed = $desktop.ReadSettings()
            if ($changed.iconSize -ne $size) { throw 'Requested icon size was not applied.' }
            $result = $desktop.Restore($before)
            $result | ConvertTo-Json
            if (-not $result.Complete) { throw "Round trip $($before.iconSize) -> $size failed." }
            Write-Output "PASS size $($before.iconSize) -> $size -> $($before.iconSize); all $($before.items.Count) coordinates exact."
        }
        $restored = $true
    } finally {
        if (-not $restored) {
            $recovery = $desktop.Restore($before)
            Write-Output ('Recovery: ' + ($recovery | ConvertTo-Json -Compress))
        }
    }
} finally { $desktop.Dispose() }
