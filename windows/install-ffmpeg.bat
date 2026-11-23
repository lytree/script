@echo off
rem ===========================================================================
rem  FFmpeg installer - batch entry point
rem  All real logic lives in install-ffmpeg.ps1 (PowerShell 7+).
rem  This wrapper only locates pwsh, forwards arguments, and surfaces the
rem  exit code.
rem
rem  Usage:
rem    install-ffmpeg.bat                          install / upgrade
rem    install-ffmpeg.bat -Force                   reinstall
rem    install-ffmpeg.bat -Proxy http://127.0.0.1:7897
rem    install-ffmpeg.bat -InstallDir E:\tools\ffmpeg -Version 9.0.2
rem    install-ffmpeg.bat /nopause                 skip the final pause
rem ===========================================================================
setlocal enabledelayedexpansion
title Install FFmpeg

set "PWSH="
if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "PWSH=%ProgramFiles%\PowerShell\7\pwsh.exe"
if exist "%ProgramFiles(x86)%\PowerShell\7\pwsh.exe" if not defined PWSH set "PWSH=%ProgramFiles(x86)%\PowerShell\7\pwsh.exe"
if not defined PWSH (
    for /f "delims=" %%I in ('where pwsh.exe 2^>nul') do (
        if not defined PWSH set "PWSH=%%I"
    )
)

if not defined PWSH (
    echo [X] PowerShell 7 ^(pwsh.exe^) not found.
    echo     Install it with:  winget install --id Microsoft.PowerShell -e
    goto :fail
)

set "PS1=%~dp0install-ffmpeg.ps1"
if not exist "%PS1%" (
    echo [X] Script not found: %PS1%
    goto :fail
)

rem ---- separate script args from wrapper args ----
set "PSARGS="
set "NOPAUSE=0"
:parse
if "%~1"=="" goto :parsed
if /i "%~1"=="/nopause" set "NOPAUSE=1" & shift & goto :parse
set "PSARGS=!PSARGS! %1"
shift
goto :parse
:parsed

echo.
echo   pwsh    : %PWSH%
echo   script  : %PS1%
echo   args    :%PSARGS%
echo.
echo   A proxy on localhost will be auto-detected. No proxy = direct.
echo.

"%PWSH%" -NoProfile -ExecutionPolicy Bypass -File "%PS1%"%PSARGS%
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
    echo [OK] Finished. Open a NEW terminal to use ffmpeg.
) else (
    rem  NOTE: with enabledelayedexpansion, "[!]" gets eaten as a variable
    rem  reference and prints "[]". Avoid "!" in echo'd text.
    echo [WARN] Exit code %RC%  ^(0=ok, 1=failed^)
    echo     Log: %TEMP%\install-ffmpeg.log
)

if "%NOPAUSE%"=="0" pause
endlocal & exit /b %RC%

:fail
echo.
if "%NOPAUSE%"=="0" pause
endlocal & exit /b 1
