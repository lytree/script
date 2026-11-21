@echo off
rem ===========================================================================
rem  FFmpeg installer (Gyan full build) - GitHub direct download via proxy
rem
rem  Why not winget? On this machine winget's own downloader stalls behind the
rem  proxy, and "--proxy" is disabled by the administrator. curl works, so
rem  this script does the download itself.
rem
rem  Usage:
rem    ffmpeg-install.bat                    auto-detect proxy
rem    ffmpeg-install.bat http://127.0.0.1:7897   explicit proxy
rem    ffmpeg-install.bat --proxy none        force direct, no proxy
rem    ffmpeg-install.bat /nopause            skip the final pause
rem ===========================================================================
setlocal
title FFmpeg Installer

set "DEST=E:\ffmpeg"
set "VER=9.0.2"
set "URL=https://github.com/GyanD/codexffmpeg/releases/download/%VER%/ffmpeg-%VER%-full_build.zip"

set "PROXY="
set "NOPAUSE=0"

:parseargs
if "%~1"=="" goto :parsed
if /i "%~1"=="/nopause" set "NOPAUSE=1" & shift & goto :parseargs
if /i "%~1"=="--proxy"   set "PROXY=none" & shift & goto :parseargs
set "PROXY=%~1"
shift
goto :parseargs
:parsed

echo.
echo   FFmpeg %VER% (Gyan full build)
echo   Target : %DEST%
echo.

if exist "%DEST%\bin\ffmpeg.exe" (
    echo [!] Already installed at %DEST%
    echo     Use /remove first if you want a clean reinstall.
    goto :finish 0
)

where curl.exe >nul 2>&1
if errorlevel 1 (
    echo [X] curl.exe not found. It ships with Windows 10 1803 and later.
    goto :finish 1
)

rem ---------------------------------------------------------------------------
rem  Auto-detect a working local proxy
rem ---------------------------------------------------------------------------
if "%PROXY%"=="none" (
    echo   Proxy : disabled ^(direct^)
) else if not "%PROXY%"=="" (
    echo   Proxy : %PROXY%
) else (
    echo   Detecting proxy...
    for %%P in (7897 7890 10809 1080 10808 1081 8888 8080 2080) do (
        if not defined PROXY (
            powershell -NoProfile -Command "if (Test-NetConnection -ComputerName 127.0.0.1 -Port %%P -InformationLevel Quiet -WarningAction SilentlyContinue) { exit 0 } else { exit 1 }" >nul 2>&1
            if not errorlevel 1 set "PROXY=http://127.0.0.1:%%P"
        )
    )
    if defined PROXY (
        echo   Proxy : %PROXY% ^(auto-detected^)
    ) else (
        echo   Proxy : none found, going direct
    )
)

rem ---------------------------------------------------------------------------
rem  Download
rem ---------------------------------------------------------------------------
set "TMPZIP=%TEMP%\ffmpeg-%VER%-full_build.zip"

echo.
echo   Downloading...
echo   %URL%
echo.

set "DLARGS="
if defined PROXY if /i not "%PROXY%"=="none" set "DLARGS=--proxy %PROXY%"

curl.exe -L %DLARGS% --connect-timeout 20 --max-time 1800 --retry 3 --retry-delay 2 ^
    -o "%TMPZIP%" "%URL%"
if errorlevel 1 (
    echo.
    echo [X] Download failed.
    echo     If you are behind a proxy, pass it explicitly, e.g.
    echo       ffmpeg-install.bat http://127.0.0.1:7897
    goto :finish 1
)

if not exist "%TMPZIP%" (
    echo [X] Download produced no file.
    goto :finish 1
)

for %%F in ("%TMPZIP%") do set "ZIPSIZE=%%~zF"
echo.
for %%F in ("%TMPZIP%") do echo   Downloaded %%~zF bytes
if %%ZIPSIZE%% LSS 10000000 (
    echo [X] File too small ^(under 10 MB^), the download is probably broken.
    del /q "%TMPZIP%" >nul 2>&1
    goto :finish 1
)

rem ---------------------------------------------------------------------------
rem  Extract
rem ---------------------------------------------------------------------------
echo.
echo   Extracting...

set "STAGE=%TEMP%\_ffmpeg_stage"
if exist "%STAGE%" rmdir /s /q "%STAGE%"
mkdir "%STAGE%" 2>nul

powershell -NoProfile -Command "Expand-Archive -LiteralPath '%TMPZIP%' -DestinationPath '%STAGE%' -Force"
if errorlevel 1 (
    echo [X] Extraction failed.
    goto :finish 1
)

rem The archive contains a single versioned folder such as
rem "ffmpeg-9.0.2-full_build". Move it to DEST.
set "INNER="
for /d %%D in ("%STAGE%\*") do set "INNER=%%~fD"
if not defined INNER (
    echo [X] Unexpected archive layout, no top-level folder found.
    goto :finish 1
)
echo   Found: %INNER%

if exist "%DEST%" rmdir /s /q "%DEST%"
move "%INNER%" "%DEST%" >nul 2>&1
if errorlevel 1 (
    echo [X] Failed to move to %DEST%
    goto :finish 1
)
rmdir /s /q "%STAGE%" 2>nul

rem ---------------------------------------------------------------------------
rem  PATH (user scope, no admin needed)
rem ---------------------------------------------------------------------------
echo.
echo   Updating user PATH...
set "BINPATH=%DEST%\bin"
powershell -NoProfile -Command ^
  "$p=[Environment]::GetEnvironmentVariable('Path','User'); $i=@(); if($p){$i=$p -split ';' | Where-Object {$_ -and $_.Trim()}}; if(-not ($i | Where-Object {$_.TrimEnd('\').ToLowerInvariant() -eq '%BINPATH%'.ToLowerInvariant()})){ [Environment]::SetEnvironmentVariable('Path', (($i + @('%BINPATH%')) -join ';'), 'User'); Write-Host '   Added to user PATH' } else { Write-Host '   Already in user PATH' }"

rem ---------------------------------------------------------------------------
rem  Verify
rem ---------------------------------------------------------------------------
echo.
echo   Verifying...
set "BAD=0"

if exist "%DEST%\bin\ffmpeg.exe" (echo     [OK ] ffmpeg.exe) else (echo     [BAD] ffmpeg.exe & set /a BAD+=1)
if exist "%DEST%\bin\ffprobe.exe" (echo     [OK ] ffprobe.exe) else (echo     [BAD] ffprobe.exe & set /a BAD+=1)
if exist "%DEST%\bin\ffplay.exe" (echo     [OK ] ffplay.exe) else (echo     [BAD] ffplay.exe & set /a BAD+=1)

"%DEST%\bin\ffmpeg.exe" -version 2>nul | find "ffmpeg version" >nul 2>&1
if errorlevel 1 (echo     [BAD] ffmpeg does not run & set /a BAD+=1) else (echo     [OK ] runs correctly)

del /q "%TMPZIP%" >nul 2>&1

echo.
if %BAD%==0 (
    echo [OK] Done. Open a NEW terminal to use ffmpeg.
) else (
    echo [!] %BAD% problem^(s^) found.
)

:finish
rem %~1 lets error paths pass an explicit exit code, e.g. "goto :finish 1".
if "%~1"=="" (
    if "%BAD%"=="" (set "BAD=0") else (set "BAD=%BAD%")
) else (
    set "BAD=%~1"
)
if "%NOPAUSE%"=="0" pause
endlocal & exit /b %BAD%
