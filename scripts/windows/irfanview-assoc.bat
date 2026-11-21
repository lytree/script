@echo off
rem ===========================================================================
rem  IrfanView image file association - install / remove
rem
rem  Writes to HKCU (user scope) on purpose:
rem    - no administrator rights needed
rem    - user scope takes priority over machine defaults
rem      (e.g. .bmp would otherwise stay on Paint.Picture)
rem
rem  Usage:
rem    irfanview-assoc.bat                    associate (default)
rem    irfanview-assoc.bat /remove            undo
rem    irfanview-assoc.bat /install /nopause  associate, no final pause
rem ===========================================================================
setlocal
title IrfanView Image Association

set "IV_EXE=D:\IrfanView\i_view64.exe"
set "PROGID=IrfanView.Image"

set "MODE=install"
set "NOPAUSE=0"

:parseargs
if "%~1"=="" goto :parsed
if /i "%~1"=="/remove"     set "MODE=remove"  & shift & goto :parseargs
if /i "%~1"=="/uninstall" set "MODE=remove"  & shift & goto :parseargs
if /i "%~1"=="/install"    set "MODE=install" & shift & goto :parseargs
if /i "%~1"=="/nopause"    set "NOPAUSE=1"   & shift & goto :parseargs
shift
goto :parseargs
:parsed

if not exist "%IV_EXE%" (
    echo [X] IrfanView not found: %IV_EXE%
    echo     Edit the IV_EXE line near the top of this script.
    goto :finish 1
)

rem Single-line list keeps the for-loop free of continuation-line pitfalls
set "EXTS=jpg jpeg jpe png bmp dib gif webp tif tiff ico cur svg svgz psd raw cr2 nef arw dng orf raf rw2 pef srw jp2 j2k jpf jpx jxl heic heif avif tga targa pcx xcf wbmp pbm pgm ppm pnm hdr exr dds"

echo.
echo   IrfanView : %IV_EXE%
echo   ProgID    : %PROGID%
echo   Mode      : %MODE%
echo.

if /i "%MODE%"=="remove" goto :remove

rem ===========================================================================
:install
echo   Registering program...
reg add "HKCU\Software\Classes\%PROGID%" /ve /t REG_SZ /d "IrfanView Image Viewer" /f >nul 2>&1
reg add "HKCU\Software\Classes\%PROGID%\shell\open\command" /ve /t REG_SZ /d "\"%IV_EXE%\" \"%%1\"" /f >nul 2>&1
reg add "HKCU\Software\Classes\%PROGID%\Capabilities" /v ApplicationName /t REG_SZ /d "IrfanView Image Viewer" /f >nul 2>&1
reg add "HKCU\Software\Classes\%PROGID%\SupportedTypes" /ve /t REG_NONE /f >nul 2>&1

set /a COUNT=0
for %%E in (%EXTS%) do (
    reg add "HKCU\Software\Classes\%PROGID%\SupportedTypes" /v ".%%E" /t REG_SZ /d "" /f >nul 2>&1
    reg add "HKCU\Software\Classes\.%%E" /ve /t REG_SZ /d "%PROGID%" /f >nul 2>&1
    reg add "HKCU\Software\Classes\.%%E\OpenWithProgids" /v "%PROGID%" /t REG_SZ /d "" /f >nul 2>&1
    set /a COUNT+=1
)

echo   Associated %COUNT% extensions.
echo.
echo   Verifying...

set /a BAD=0
for %%E in (jpg png bmp gif webp svg ico psd raw) do (
    reg query "HKCU\Software\Classes\.%%E" /ve 2>nul | find "%PROGID%" >nul 2>&1
    if errorlevel 1 (
        echo     [BAD] .%%E
        set /a BAD+=1
    ) else (
        echo     [OK ] .%%E
    )
)

reg query "HKCU\Software\Classes\%PROGID%\shell\open\command" /ve 2>nul | find "%IV_EXE%" >nul 2>&1
if errorlevel 1 (
    echo     [BAD] open command
    set /a BAD+=1
) else (
    echo     [OK ] open command
)

echo.
if %BAD%==0 (
    echo [OK] Done. Open a NEW Explorer window to see the changes.
) else (
    echo [!] %BAD% item^(s^) failed to verify.
)
goto :finish %BAD%

rem ===========================================================================
:remove
echo   Removing association...
reg delete "HKCU\Software\Classes\%PROGID%" /f >nul 2>&1

for %%E in (%EXTS%) do (
    reg delete "HKCU\Software\Classes\.%%E\OpenWithProgids\%PROGID%" /f >nul 2>&1
)

echo [OK] Association removed.
echo   Note: extension keys themselves were left in place, so Windows
echo         falls back to the machine-level default for each format.
goto :finish 0

rem ===========================================================================
:finish
if "%~1" neq "" set "RC=%~1" else set "RC=0"
echo.
if "%NOPAUSE%"=="0" pause
endlocal & exit /b %RC%
