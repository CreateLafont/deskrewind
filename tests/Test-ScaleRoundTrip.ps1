#Requires -Version 7.0
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -Path (Join-Path $root 'native\ShellDesktop.cs'),(Join-Path $root 'native\DisplayScale.cs') -CompilerOptions '/nowarn:9191'
$shell=[ShellDesktop]::new()
$desktop=$shell.Capture()
$shell.Dispose()
$before=[DisplayScale]::Capture()
$backup=Join-Path $root ('test-logs\scale-before-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.json')
@{monitors=$before;desktop=$desktop}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $backup -Encoding utf8
try {
    foreach($monitor in $before){
        $target=[DisplayScale]::Capture()
        $changed=$target|Where-Object identity -eq $monitor.identity
        $changed.percent=if($monitor.percent -gt 100){$monitor.percent-25}else{125}
        Write-Output "Testing $($monitor.device): $($monitor.percent)% -> $($changed.percent)%"
        [DisplayScale]::Apply($target,$false)
        $live=[DisplayScale]::Capture()
        if(-not [DisplayScale]::SameScale($target,$live)){throw 'Scale did not apply.'}
        $live|Select-Object device,percent,width,height|Format-Table
        [DisplayScale]::Apply($before,$false)
        $s=[ShellDesktop]::new()
        try {$result=$s.Restore($desktop);if(-not $result.Complete){throw ($result|ConvertTo-Json)}}finally{$s.Dispose()}
        Write-Output "PASS $($monitor.device) scale restored, $($desktop.items.Count) original positions exact."
    }
}finally{
    [DisplayScale]::Apply($before,$true)
    $s=[ShellDesktop]::new()
    try {$result=$s.Restore($desktop); if(-not $result.Complete){throw ('Recovery failed; baseline: '+$backup)}}finally{$s.Dispose()}
    Write-Output 'Original scales, icon size and positions restored.'
}
