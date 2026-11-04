@echo off
rem Build dist\HoanngFleet.exe with the C# compiler that ships with Windows (.NET Framework 4.x). Nothing to install.
rem Usage: build.cmd [output.exe]
rem Embedded resources: assets\logo-128.png (header) and scripts\setup-ubuntu-agent-vm.sh
rem (pinned copy of github.com/hoanng-dev/ubuntu-agent-vm @ fe5b44c). assets\logo.ico = exe/window icon.
setlocal
set OUT=%~1
if "%OUT%"=="" (
  if not exist "%~dp0dist" mkdir "%~dp0dist"
  set OUT=%~dp0dist\HoanngFleet.exe
)
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /codepage:65001 /optimize+ ^
  /win32icon:"%~dp0assets\logo.ico" /resource:"%~dp0assets\logo-128.png",logo-128.png ^
  /resource:"%~dp0scripts\setup-ubuntu-agent-vm.sh",setup-ubuntu-agent-vm.sh ^
  /out:"%OUT%" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Runtime.Serialization.dll ^
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "%~dp0src\HoanngFleet.cs"
if errorlevel 1 exit /b 1
echo Built: %OUT%
