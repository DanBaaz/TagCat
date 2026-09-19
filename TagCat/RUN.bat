@echo off
title TagCat - Run
cd /d "%~dp0"

echo.
echo  TagCat
echo  ============
echo.

where dotnet >nul 2>nul
if errorlevel 1 goto nosdk

rem This project targets net9.0-windows10.0.19041.0, so a .NET 9 SDK is required.
rem An older SDK gives a confusing "framework not found" error, so check up front.
dotnet --list-sdks | findstr /b "9." >nul
if errorlevel 1 goto nonet9

echo  Building...
echo.
dotnet build -c Debug -v quiet --nologo
if errorlevel 1 goto buildfail

echo.
echo  Starting...
echo.
dotnet run -c Debug --no-build
if errorlevel 1 (
    echo.
    echo  The app closed with an error.
    pause
)
exit /b 0

:nosdk
echo  The .NET SDK is not installed.
echo.
echo  Get the .NET 9 SDK ^(x64^) from:
echo     https://dotnet.microsoft.com/download/dotnet/9.0
echo.
pause
exit /b 1

:nonet9
echo  A .NET SDK is installed, but not version 9.
echo.
echo  TagCat targets net9.0-windows10.0.19041.0 since the Duplicate
echo  Finder was merged in. Installed SDKs:
echo.
dotnet --list-sdks
echo.
echo  Install the .NET 9 SDK ^(x64^) from:
echo     https://dotnet.microsoft.com/download/dotnet/9.0
echo  Existing .NET 8 apps keep working; 9 installs alongside.
echo.
pause
exit /b 1

:buildfail
echo.
echo  The build failed. The messages above say why.
echo.
pause
exit /b 1
