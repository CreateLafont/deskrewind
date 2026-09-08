#Requires -Version 7.0
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -Path (Join-Path $root 'native\ShellDesktop.cs') -CompilerOptions '/nowarn:9191'
$options=[Text.Json.JsonSerializerOptions]::new(); $options.IncludeFields=$true
$profile=(Get-Content (Join-Path $root 'test-logs\manual-test-layouts.json') -Raw|ConvertFrom-Json).profiles|Sort-Object savedAt -Descending|Select-Object -First 1
$target=[Text.Json.JsonSerializer]::Deserialize(($profile.shellState|ConvertTo-Json -Depth 10),[ShellLayoutState],$options)
$s=[ShellDesktop]::new(); $before=$s.Capture()
$before|ConvertTo-Json -Depth 10|Set-Content (Join-Path $root ('test-logs\refresh-before-'+(Get-Date -Format yyyyMMdd-HHmmss)+'.json')) -Encoding utf8
try {
    $s.Restore($target)|ConvertTo-Json -Compress|Write-Output
    for($n=0;$n -le 4;$n++){
        if($n -gt 0){$s.Refresh();Start-Sleep -Milliseconds 600}
        $now=$s.Capture(); $result=[ShellDesktop]::Compare($target,$now)
        $map=@{};foreach($i in $now.items){$map[$i.identity]=$i}
        $deltas=foreach($i in $target.items){if($map.ContainsKey($i.identity)){[pscustomobject]@{dx=$map[$i.identity].x-$i.x;dy=$map[$i.identity].y-$i.y}}}
        Write-Output "Refresh=$n size=$($now.iconSize) grid=$($now.spacingX)x$($now.spacingY) wrong=$($result.misplaced)"
        if(-not $result.Complete){throw "Refresh $n did not preserve the original layout."}
        $deltas|Group-Object dx,dy|Sort-Object Count -Descending|Select-Object -First 4 Count,Name|Format-Table
    }
} finally {
    $r=$s.Restore($before);$s.Dispose()
    if(-not $r.Complete){throw 'Baseline recovery failed.'}
    Write-Output 'Baseline restored.'
}
