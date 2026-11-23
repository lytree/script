@echo off
rem ===========================================================================
rem  PotPlayer video file association - batch entry point
rem  All real logic lives in potplayer-assoc.ps1 (PowerShell 7+).
rem  This wrapper only locates pwsh, forwards arguments, and surfaces the
rem  exit code.
rem
rem  Usage:
rem    potplayer-assoc.bat                              associate (auto-detect)
rem    potplayer-assoc.bat /remove                      undo
rem    potplayer-assoc.bat -ExePath "D:\PotPlayer\PotPlayerMini64.exe"
rem    potplayer-assoc.bat /nopause                     skip the final pause
rem ===========================================================================
setlocal enabledelayedexpansion
title PotPlayer Video Association

set "PWSH="
if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "PWSH=%ProgramFiles%\PowerShell\7\pwsh.exe"
if exist "%ProgramFiles(x86)%\PowerShell\7\pwsh.exe" if not defined PWSH set "PWSH=%ProgramFiles(x86)%\PowerShell\7\pwsh.exe"
if not defined PWSH (
    for /f "delims=" %%I in ('where pwsh.exe 2^>nul') do (
        if not defined PWSH set "PWSH=%%I"
    )
)

set "NOPAUSE=0"
if not defined PWSH (
    echo [X] PowerShell 7 ^(pwsh.exe^) not found.
    echo     Install it with:  winget install --id Microsoft.PowerShell -e
    goto :fail
)

set "PS1=%~dp0potplayer-assoc.ps1"
if not exist "%PS1%" (
    echo [X] Script not found: %PS1%
    goto :fail
)

rem ---- forward script args ----
rem  cmd.exe treats "," as an argument separator, so a shift-loop would split
rem  "-Extensions mp4,mkv" into two args and "pwsh -File" would then reject the
rem  extra one as positional. So the raw line is forwarded verbatim (quoting
rem  preserved). Do NOT pipe through findstr: a comma in the payload makes it
rem  read "/a" as a switch. Pure string substitution instead.
rem  pwsh -File accepts "-Switch" but not "/Switch"; accept both here.
set "PSARGS=%*"
set "NOPAUSE=0"

:scan
set "SCAN=%PSARGS%"
call set "SCAN=%%SCAN:/nopause=%%"
call set "SCAN=%%SCAN:-NoPause=%%"
if "%SCAN%"=="%PSARGS%" goto :scanned
call set "PSARGS=%%PSARGS: /nopause=%%"
call set "PSARGS=%%PSARGS:/nopause=%%"
call set "PSARGS=%%PSARGS: -NoPause=%%"
call set "PSARGS=%%PSARGS:-NoPause=%%"
set "NOPAUSE=1"
goto :scan
:scanned

echo.
echo   pwsh    : %PWSH%
echo   script  : %PS1%
echo   args    : %PSARGS%
echo.

"%PWSH%" -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %PSARGS%
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
    echo [OK] Finished. Open a NEW Explorer window to see the changes.
) else (
    rem  NOTE: with enabledelayedexpansion, "[!]" gets eaten as a variable
    rem  reference and prints "[]". Avoid "!" in echo'd text.
    echo [WARN] Exit code %RC%  ^(0=ok, 1=failed, 3=PowerShell 5.1^)
    echo     Log: %TEMP%\potplayer-assoc.log
)

if "%NOPAUSE%"=="0" pause
endlocal & exit /b %RC%

:fail
echo.
if "%NOPAUSE%"=="0" pause
endlocal & exit /b 1
