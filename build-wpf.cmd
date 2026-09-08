@echo off
setlocal
set "Csc=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "Framework=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319"

if not exist "%Csc%" (
  echo Unable to find the .NET Framework C# compiler: %Csc%
  exit /b 1
)

"%Csc%" /nologo /target:winexe /platform:x64 /main:DeskRewind.Wpf.Program /win32icon:"%~dp0Assets\TitleBar\DeskRewind.ico" /out:"%~dp0DeskRewind.exe" ^
  /r:"%Framework%\WPF\PresentationFramework.dll" ^
  /r:"%Framework%\WPF\PresentationCore.dll" ^
  /r:"%Framework%\WPF\WindowsBase.dll" ^
  /r:"%Framework%\System.Xaml.dll" ^
  /r:"%Framework%\System.Web.Extensions.dll" ^
  /r:"%Framework%\System.Windows.Forms.dll" ^
  /r:"%Framework%\System.Drawing.dll" ^
  /resource:"%~dp0Assets\Fonts\OPPOSans-B-UI.ttf",DeskRewind.Assets.Fonts.OPPOSans-B-UI.ttf ^
  /resource:"%~dp0Assets\Fonts\OPPOSans-R-UI.ttf",DeskRewind.Assets.Fonts.OPPOSans-R-UI.ttf ^
  /resource:"%~dp0Assets\Icons\add.svg",DeskRewind.Assets.Icons.add.svg ^
  /resource:"%~dp0Assets\Icons\arrange.svg",DeskRewind.Assets.Icons.arrange.svg ^
  /resource:"%~dp0Assets\Icons\close.svg",DeskRewind.Assets.Icons.close.svg ^
  /resource:"%~dp0Assets\Icons\delete.svg",DeskRewind.Assets.Icons.delete.svg ^
  /resource:"%~dp0Assets\Icons\full.svg",DeskRewind.Assets.Icons.full.svg ^
  /resource:"%~dp0Assets\Icons\min.svg",DeskRewind.Assets.Icons.min.svg ^
  /resource:"%~dp0Assets\Icons\monitor.svg",DeskRewind.Assets.Icons.monitor.svg ^
  /resource:"%~dp0Assets\Icons\overwrite.svg",DeskRewind.Assets.Icons.overwrite.svg ^
  /resource:"%~dp0Assets\Icons\refresh.svg",DeskRewind.Assets.Icons.refresh.svg ^
  /resource:"%~dp0Assets\Icons\rename.svg",DeskRewind.Assets.Icons.rename.svg ^
  /resource:"%~dp0Assets\Icons\restore.svg",DeskRewind.Assets.Icons.restore.svg ^
  /resource:"%~dp0Assets\TitleBar\DeskRewind.ico",DeskRewind.Assets.TitleBar.DeskRewind.ico ^
  /resource:"%~dp0Assets\TitleBar\IconTitle.png",DeskRewind.Assets.TitleBar.IconTitle.png ^
  "%~dp0Program.cs" "%~dp0KeeperWindow.cs" "%~dp0native\DeskRewind.Native.cs" "%~dp0native\ShellDesktop.cs" "%~dp0native\DisplayScale.cs"
set "CompileExit=%ERRORLEVEL%"
if not "%CompileExit%"=="0" exit /b %CompileExit%

endlocal
