param([Reflection.Assembly]$Assembly, [switch]$IncludeScale)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$store = Join-Path $root ('test-logs\integration-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
$execute = $Assembly.GetType('Program').GetMethod('Execute', [Reflection.BindingFlags]'Static,NonPublic')
function Invoke-Engine([string[]]$Arguments) {
    $writer = [IO.StringWriter]::new()
    $errors = [IO.StringWriter]::new()
    $oldOut = [Console]::Out; $oldError = [Console]::Error
    try {
        [Console]::SetOut($writer); [Console]::SetError($errors)
        $argsWithStore = [string[]]($Arguments + @('--store', $store))
        $code = $execute.Invoke($null, [object[]]@(,$argsWithStore))
        $capturedError = $errors.ToString()
        $capturedOutput = $writer.ToString()
    } finally { [Console]::SetOut($oldOut); [Console]::SetError($oldError) }
    if ($code -ne 0) { throw $capturedError }
    return $capturedOutput
}
$shell = [ShellDesktop]::new()
$before = $shell.Capture()
$beforeScale = [DisplayScale]::Capture()
$before | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath ($store + '.desktop-before.json') -Encoding utf8
try {
    Invoke-Engine @('save','--name','回归测试') | Write-Output
    $saved = (Get-Content -LiteralPath $store -Raw | ConvertFrom-Json).profiles[0]
    if ($saved.shellState.iconSize -ne $before.iconSize -or $saved.shellState.items.Count -ne $before.items.Count) { throw 'Save did not persist live Shell state.' }
    $listing = @( (Invoke-Engine @('list','--json') | ConvertFrom-Json) )[0]
    if (-not $listing.iconGridMatch) { throw 'Unchanged desktop misdetected.' }
    foreach ($delta in @(-4, 4, 24)) {
        $shell.ApplySize($before.viewMode, $before.iconSize + $delta)
        $listing = @( (Invoke-Engine @('list','--json') | ConvertFrom-Json) )[0]
        if ($listing.iconGridMatch) { throw 'Icon-size change was not detected.' }
        # The same message selector that the Apply button calls.
        $profile = [Activator]::CreateInstance($Assembly.GetType('DeskRewind.Wpf.LayoutProfile'), $true)
        foreach ($field in @('id','name','hardwareMatch','systemScaleKnown','systemScaleMatch','iconGridKnown','iconGridMatch')) {
            $profile.GetType().GetProperty($field).SetValue($profile,$listing.$field)
        }
        $selector = $Assembly.GetType('DeskRewind.Wpf.KeeperWindow').GetMethod('RestoreMessage',[Reflection.BindingFlags]'Static,NonPublic')
        $prompt = $selector.Invoke($null,@($profile))
        if ($prompt -ne '当前图标大小与保存时不同，是否恢复保存时的图标大小并还原图标排列？') { throw "Wrong prompt: $prompt" }
        $rejected = $false
        try { Invoke-Engine @('restore','--profile',$saved.id) | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw 'Restore changed an unconfirmed environment.' }
        Invoke-Engine @('restore','--profile',$saved.id,'--force') | Write-Output
        $result = [ShellDesktop]::Compare($before,$shell.Capture())
        if (-not $result.Complete) { throw ($result | ConvertTo-Json -Compress) }
        for ($refresh=1; $refresh -le 5; $refresh++) {
            $shell.Refresh(); Start-Sleep -Milliseconds 250
            $checked=[ShellDesktop]::Compare($before,$shell.Capture())
            if(-not $checked.Complete){throw "Refresh $refresh drifted after icon-size restore: $($checked|ConvertTo-Json -Compress)"}
        }
        Write-Output "PASS integrated save/detect/prompt/restore: size $($before.iconSize) -> $($before.iconSize + $delta) -> $($before.iconSize); exact positions $($result.matched)/$($before.items.Count)."
        Write-Output 'PASS five further desktop refreshes: zero drift.'
    }
    # A drift in an item that previously passed must not escape final verification.
    $simulated = $shell.Capture(); $simulated.items[0].x += 10
    if ([ShellDesktop]::Compare($before,$simulated).misplaced -ne 1) { throw 'Full verification misses drift.' }
    Write-Output 'PASS post-restore full verification detects a 10px drift.'
    $originalStoreText = Get-Content -LiteralPath $store -Raw
    try {
        $hardwareDocument = $originalStoreText | ConvertFrom-Json
        $hardwareDocument.profiles[0].hardware = 'physical-v1|AUDIT-MISMATCH'
        $hardwareDocument.profiles[0].topology = 'physical-v1|AUDIT-MISMATCH'
        $hardwareDocument.profiles[0].shellState.items += [pscustomobject]@{ identity='audit-missing-item'; name='审计缺失项目'; x=64; y=64 }
        $hardwareDocument | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $store -Encoding utf8
        $hardwareListing = @((Invoke-Engine @('list','--json') | ConvertFrom-Json))[0]
        if ($hardwareListing.hardwareMatch) { throw 'Hardware mismatch was not detected.' }
        $hardwareProfile = [Activator]::CreateInstance($Assembly.GetType('DeskRewind.Wpf.LayoutProfile'), $true)
        foreach ($field in @('id','name','hardwareMatch','systemScaleKnown','systemScaleMatch','iconGridKnown','iconGridMatch','environmentToken')) {
            $hardwareProfile.GetType().GetProperty($field).SetValue($hardwareProfile,$hardwareListing.$field)
        }
        $hardwarePrompt = $selector.Invoke($null,@($hardwareProfile))
        if ($hardwarePrompt -ne '当前显示器硬件配置与保存时不同，无法完全恢复保存时的图标排列，可能产生更严重的错位，是否继续？') { throw "Wrong hardware prompt: $hardwarePrompt" }
        $hardwareOutput = Invoke-Engine @('restore','--profile',$saved.id,'--force','--expected',$hardwareListing.environmentToken)
        if ($hardwareOutput -notmatch '已尝试恢复') { throw "Hardware best-effort output missing: $hardwareOutput" }
        $hardwareAfter = $shell.Capture()
        $hardwareResult = [ShellDesktop]::Compare($before,$hardwareAfter)
        if (-not $hardwareResult.Complete) { throw ('Hardware best-effort disturbed existing items: ' + ($hardwareResult | ConvertTo-Json -Compress)) }
        Write-Output 'PASS hardware-mismatch prompt and best-effort partial restore keep existing items.'
    } finally {
        [IO.File]::WriteAllText($store,$originalStoreText,[Text.UTF8Encoding]::new($false))
    }
    if ($IncludeScale) {
        foreach ($kind in @('primary','secondary','combined')) {
            $scales = [DisplayScale]::Capture()
            $index = if ($kind -eq 'secondary') { 1 } else { 0 }
            if ($index -ge $scales.Count) { continue }
            $scales[$index].percent = if ($scales[$index].percent -gt 100) { $scales[$index].percent - 25 } else { 125 }
            [DisplayScale]::Apply($scales,$false)
            if ($kind -eq 'combined') {
                $s=[ShellDesktop]::new()
                try { $s.ApplySize($before.viewMode,$before.iconSize+4) } finally { $s.Dispose() }
            }
            $listing = @((Invoke-Engine @('list','--json') | ConvertFrom-Json))[0]
            if (-not $listing.hardwareMatch) { throw 'System scaling was misclassified as a hardware change.' }
            if ($listing.systemScaleMatch) { throw 'System scaling change was missed.' }
            $profile = [Activator]::CreateInstance($Assembly.GetType('DeskRewind.Wpf.LayoutProfile'), $true)
            foreach ($field in @('id','name','hardwareMatch','systemScaleKnown','systemScaleMatch','iconGridKnown','iconGridMatch')) {
                $profile.GetType().GetProperty($field).SetValue($profile,$listing.$field)
            }
            $prompt=$selector.Invoke($null,@($profile))
            $expected=if($kind -eq 'combined'){'当前系统缩放、图标大小与保存时不同，是否恢复保存时的系统缩放、图标大小和图标排列？'}else{'当前系统缩放与保存时不同，是否恢复保存时的系统缩放并还原图标排列？'}
            if ($prompt -ne $expected) { throw "Wrong $kind prompt: $prompt" }
            Invoke-Engine @('restore','--profile',$saved.id,'--force','--expected',$listing.environmentToken) | Write-Output
            if (-not [DisplayScale]::SameScale($beforeScale,[DisplayScale]::Capture())) { throw 'Scale restoration failed.' }
            $s=[ShellDesktop]::new()
            try {
                for($refresh=0;$refresh -le 5;$refresh++){
                    if($refresh -gt 0){$s.Refresh();Start-Sleep -Milliseconds 250}
                    $r=[ShellDesktop]::Compare($before,$s.Capture()); if(-not $r.Complete){throw ($r|ConvertTo-Json)}
                }
            } finally { $s.Dispose() }
            Write-Output "PASS integrated $kind scaling: correct prompt, original scales and $($before.items.Count)/$($before.items.Count) coordinates restored."
        }
        $scales = [DisplayScale]::Capture()
        $scales[0].percent = if($scales[0].percent -gt 100){$scales[0].percent-25}else{125}
        [DisplayScale]::Apply($scales,$false)
        $s=[ShellDesktop]::new()
        try {$preFailure=$s.Capture()} finally {$s.Dispose()}
        $document=Get-Content -LiteralPath $store -Raw|ConvertFrom-Json
        $document.profiles[0].shellState.iconSize=999
        $document|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $store -Encoding utf8
        $failed=$false
        try {Invoke-Engine @('restore','--profile',$saved.id,'--force')|Out-Null}catch{$failed=$true}
        if(-not $failed){throw 'Invalid icon target incorrectly succeeded.'}
        if(-not [DisplayScale]::SameScale($scales,[DisplayScale]::Capture())){throw 'Failure did not roll back scale.'}
        $s=[ShellDesktop]::new()
        try {if(-not [ShellDesktop]::Compare($preFailure,$s.Capture()).Complete){throw 'Failure did not roll back desktop.'}} finally {$s.Dispose()}
        Write-Output 'PASS failed icon restore rolls back changed scales and exact pre-operation desktop.'
    }
} finally {
    if ($IncludeScale) { [DisplayScale]::Apply($beforeScale,$true) }
    $result = $shell.Restore($before)
    $shell.Dispose()
    if (-not $result.Complete) { throw ('Desktop recovery failed; baseline: ' + $store + '.desktop-before.json') }
    Write-Output 'Original desktop restored and verified.'
}
