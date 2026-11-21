@echo off
chcp 65001 >nul
setlocal

set "PWSH=C:\Program Files\PowerShell\7\pwsh.exe"
set "SCRIPT=%~dp0set-system-env-pwsh7.ps1"

if not exist "%PWSH%" (
    echo [X] PowerShell 7 not found: %PWSH%
    echo     Install first: winget install --id Microsoft.PowerShell -e
    pause
    exit /b 1
)

echo.
echo   Run as administrator: set-system-env-pwsh7.ps1
echo   Machine-scope env vars: UV_* / VCPKG_ROOT / MAVEN_HOME / JAVA_HOME / NODE_HOME
echo.
echo   A UAC prompt will appear. Click "Yes".
echo.

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [i] Not elevated, requesting UAC...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo [i] Elevated. Running...
echo.

"%PWSH%" -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
set "RC=%ERRORLEVEL%"

echo.
if %RC% equ 0 (
    echo [OK] All done. Open a NEW terminal for the env vars to take effect.
) else (
    echo [!] Exit code %RC%  (0=ok, 2=not admin, 1=has mismatches)
)

echo.
echo Log: %TEMP%\set-system-env.log
pause
exit /b %RC%
