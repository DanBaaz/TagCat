@echo off
title TagCat - Build release
cd /d "%~dp0"

echo.
echo  TagCat - Full release build
echo  ============================
echo.
echo  Runs both steps in order: publishes the app, then compiles the installer.
echo  Equivalent to running MAKE-INSTALLER-PACKAGE.bat and then opening TagCat.iss
echo  in Inno Setup and pressing Compile.
echo.

rem ---- Step 1: the app itself -------------------------------------------

where dotnet >nul 2>nul
if errorlevel 1 (
    echo  The .NET SDK is not installed.
    echo  Get the .NET 9 SDK from:  https://dotnet.microsoft.com/download/dotnet/9.0
    echo.
    pause
    exit /b 1
)

rem Checked BEFORE the long publish step rather than after, so a missing Inno Setup
rem is reported in seconds instead of after waiting through a full build.
set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC for /f "delims=" %%I in ('where ISCC.exe 2^>nul') do set "ISCC=%%I"

if not defined ISCC (
    echo  Inno Setup is not installed, or ISCC.exe could not be found.
    echo  Get it from:  https://jrsoftware.org/isinfo.php
    echo.
    echo  If it is installed somewhere unusual, you can still build the app on its own
    echo  with MAKE-INSTALLER-PACKAGE.bat and compile TagCat.iss by hand.
    echo.
    pause
    exit /b 1
)

echo  Found Inno Setup at:
echo     %ISCC%
echo.

if exist "InstallerPackage" rmdir /s /q "InstallerPackage"

echo  [1/2] Publishing the app (framework-dependent - no bundled runtime)...
echo.
dotnet publish -c Release -r win-x64 --self-contained false ^
    -p:PublishSingleFile=false ^
    -o "InstallerPackage\app" ^
    --nologo

if errorlevel 1 (
    echo.
    echo  The build failed. The messages above say why.
    pause
    exit /b 1
)

echo.
echo  Removing bundled LibVLC - the installer fetches this separately, and only if
echo  no VLC is already on the machine...
if exist "InstallerPackage\app\libvlc.dll" del /q "InstallerPackage\app\libvlc.dll"
if exist "InstallerPackage\app\libvlccore.dll" del /q "InstallerPackage\app\libvlccore.dll"
if exist "InstallerPackage\app\plugins" rmdir /s /q "InstallerPackage\app\plugins"
if exist "InstallerPackage\app\libvlc" rmdir /s /q "InstallerPackage\app\libvlc"

echo.
echo  Package size:
powershell -Command "'{0:N1} MB' -f ((Get-ChildItem -Path 'InstallerPackage\app' -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB)"

rem ---- Step 2: the installer --------------------------------------------

echo.
echo  [2/2] Compiling the installer...
echo.
"%ISCC%" "TagCat.iss"

if errorlevel 1 (
    echo.
    echo  The installer failed to compile. The messages above say why.
    pause
    exit /b 1
)

echo.
echo  ============================================================
echo   Done. The installer is in the Output folder:
echo.
dir /b "Output\*.exe" 2>nul
echo.
echo   That .exe is what goes on a GitHub Release - attached as a
echo   binary, not committed into the repository itself.
echo  ============================================================
echo.
pause
