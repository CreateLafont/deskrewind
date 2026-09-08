#Requires -Version 7.0
param([ValidateSet('UI','Inspect','Integration','ScaleIntegration','UiTest')][string]$Mode = 'UI')
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run using pwsh -STA -File.' }
$env:DESKREWIND_TEST_ROOT = $PSScriptRoot
$testDir = Join-Path $PSScriptRoot 'test-logs'
[IO.Directory]::CreateDirectory($testDir) | Out-Null
$env:DESKREWIND_TEST_STORE = Join-Path $testDir 'manual-test-layouts.json'
if (-not (Test-Path -LiteralPath $env:DESKREWIND_TEST_STORE)) {
    [IO.File]::WriteAllText($env:DESKREWIND_TEST_STORE, '{"format":"DeskRewind.Profiles/v3","profiles":[]}')
}
foreach ($name in @('PresentationFramework','PresentationCore','WindowsBase','System.Windows.Forms','System.Drawing.Common','System.Text.Json')) { [Reflection.Assembly]::Load($name) | Out-Null }
$references = @([IO.Directory]::GetFiles((Join-Path $PSHOME 'ref'), '*.dll')) + @(
    'PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll','System.Windows.Forms.dll','System.Windows.Forms.Primitives.dll','System.Drawing.Common.dll','System.Xaml.dll','System.Windows.Extensions.dll','Microsoft.Win32.SystemEvents.dll','System.Security.Permissions.dll','UIAutomationTypes.dll','UIAutomationProvider.dll','UIAutomationClient.dll','System.IO.Packaging.dll'
    | ForEach-Object { Join-Path $PSHOME $_ } | Where-Object { Test-Path -LiteralPath $_ }
)
$references += @([IO.Directory]::GetFiles($PSHOME, 'System.Private.Windows*.dll'))
$sources = @('Program.cs','KeeperWindow.cs','native\DeskRewind.Native.cs','native\ShellDesktop.cs','native\DisplayScale.cs','tests\JsonCompatibility.cs','tests\SourceUiTest.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
Add-Type -Path $sources -ReferencedAssemblies $references -CompilerOptions '/define:POWERSHELL_TEST','/nowarn:0618,9191,0169,0414' -IgnoreWarnings
$assembly = [ShellDesktop].Assembly
if ($Mode -eq 'Inspect') {
    $shell = [ShellDesktop]::new()
    try { $shell.ReadSettings() | ConvertTo-Json } finally { $shell.Dispose() }
    return
}
if ($Mode -eq 'Integration') { & (Join-Path $PSScriptRoot 'tests\Test-Integration.ps1') -Assembly $assembly; return }
if ($Mode -eq 'ScaleIntegration') { & (Join-Path $PSScriptRoot 'tests\Test-Integration.ps1') -Assembly $assembly -IncludeScale; return }
if ($Mode -eq 'UiTest') {
    $env:DESKREWIND_TEST_STORE = Join-Path $testDir ('ui-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
    [DeskRewind.Wpf.SourceUiTest]::Run($testDir)
    return
}
Write-Host "Testing current source in memory; layouts: $env:DESKREWIND_TEST_STORE"
$entry = $assembly.GetType('DeskRewind.Wpf.Program').GetMethod('Main', [Reflection.BindingFlags]'NonPublic,Static')
try { $entry.Invoke($null, @()) | Out-Null } catch { throw $_.Exception.GetBaseException() }
