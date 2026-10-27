@echo off
rem ============================================================
rem  Build CdCleaner.exe with the built-in .NET Framework C# compiler.
rem  Keep this file ASCII-only.
rem ============================================================
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] csc.exe not found. .NET Framework 4.8 is required.
    pause
    exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 ^
  /win32icon:src\app.ico ^
  /win32manifest:src\app.manifest ^
  /out:CdCleaner.exe ^
  src\CdCleaner.cs

if errorlevel 1 (
    echo.
    echo Build FAILED.
    pause
    exit /b 1
)

echo.
echo Build OK: CdCleaner.exe
pause
