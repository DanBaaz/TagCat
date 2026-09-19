@echo off
title TagCat - Build standalone exe
cd /d "%~dp0"

echo.
echo  Building a standalone TagCat you can run without this folder
echo  and without the .NET runtime installed. Takes a few minutes and
echo  produces a large file, because it bundles everything.
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo  The .NET SDK is not installed.
    echo  Get the .NET 9 SDK from:  https://dotnet.microsoft.com/download/dotnet/9.0
    echo.
    pause
    exit /b 1
)

dotnet publish -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%~dp0StandaloneApp" ^
    --nologo

if errorlevel 1 (
    echo.
    echo  The build failed. The messages above say why.
    pause
    exit /b 1
)

echo.
echo  Done. Your app is here:
echo.
echo     %~dp0StandaloneApp\TagCat.exe
echo.
echo  Copy that whole StandaloneApp folder anywhere, or make a desktop
echo  shortcut to the exe. The Duplicate Finder is inside it, under
echo  Duplicate Tools in the right-hand panel.
echo.
pause
