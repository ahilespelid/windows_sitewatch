@echo off
rem SiteWatch: build with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No SDK needed. Result: SiteWatch.exe next to this file.
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe from .NET Framework 4 not found
  exit /b 1
)
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:SiteWatch.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Net.Http.dll /r:System.Security.dll ^
  main.cs
if errorlevel 1 exit /b 1
echo OK: SiteWatch.exe
