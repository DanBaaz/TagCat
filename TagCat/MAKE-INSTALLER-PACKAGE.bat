@echo off
title TagCat - Build installer package
cd /d "%~dp0"

echo.
echo  Building the installer package - a small download that fetches the
echo  .NET Runtime and VLC itself on first run, instead of bundling them.
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo  The .NET SDK is not installed.
    echo  Get the .NET 9 SDK from:  https://dotnet.microsoft.com/download/dotnet/9.0
    echo.
    pause
    exit /b 1
)

if exist "InstallerPackage" rmdir /s /q "InstallerPackage"

echo  Publishing (framework-dependent - no bundled runtime)...
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
echo  Removing bundled LibVLC - Install.bat fetches this separately, only if
echo  no VLC is already on the machine...
if exist "InstallerPackage\app\libvlc.dll" del /q "InstallerPackage\app\libvlc.dll"
if exist "InstallerPackage\app\libvlccore.dll" del /q "InstallerPackage\app\libvlccore.dll"
if exist "InstallerPackage\app\plugins" rmdir /s /q "InstallerPackage\app\plugins"
if exist "InstallerPackage\app\libvlc" rmdir /s /q "InstallerPackage\app\libvlc"

echo.
echo  Package size:
powershell -Command "'{0:N1} MB' -f ((Get-ChildItem -Path 'InstallerPackage\app' -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB)"

copy /y "Install.bat" "InstallerPackage\Install.bat" >nul
copy /y "Install.ps1" "InstallerPackage\Install.ps1" >nul

echo.
echo  Done. The installer package is here:
echo.
echo     %~dp0InstallerPackage
echo.
echo  Zip that whole folder and share it - Install.bat is what people run.
echo  This has not been tested end-to-end on a real machine; try it on a
echo  clean Windows install (or a VM) before relying on it.
echo.
pause
