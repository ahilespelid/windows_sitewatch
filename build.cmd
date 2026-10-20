@echo off
rem SiteWatch: build with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No SDK needed. Result: SiteWatch.exe next to this file.
rem app.ico goes into the exe resources: that is the icon Explorer, the taskbar and shortcuts show.
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe from .NET Framework 4 not found
  exit /b 1
)
if not exist app.ico (
  echo app.ico not found next to build.cmd
  exit /b 1
)
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:SiteWatch.exe /win32icon:app.ico ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Net.Http.dll /r:System.Security.dll ^
  main.cs
if errorlevel 1 exit /b 1
echo OK: SiteWatch.exe
