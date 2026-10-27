@echo off
rem ============================================================
rem  C drive cleaner assistant v1.1 - launcher
rem  Keep this file ASCII-only.
rem ============================================================
setlocal
cd /d "%~dp0"

if exist "CdCleaner.exe" goto :run

echo [ERROR] CdCleaner.exe not found in this folder.
echo Please keep this launcher and CdCleaner.exe together.
pause
exit /b 1

:run
start "" "%~dp0CdCleaner.exe"
exit /b 0