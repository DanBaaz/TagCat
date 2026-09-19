@echo off
title TagCat - Clean Rebuild
cd /d "%~dp0"

echo.
echo  TagCat - Clean Rebuild
echo  ===========================
echo.
echo  Deletes bin\ and obj\ then rebuilds from scratch.
echo  Use this when swapping in a new version zip over an old folder, since
echo  stale build output is the usual cause of "my change didn't apply".
echo.

if exist "bin" rmdir /s /q "bin"
if exist "obj" rmdir /s /q "obj"
echo  Cleaned.
echo.

dotnet build -c Debug -v quiet --nologo
if errorlevel 1 (
    echo.
    echo  The build failed. The messages above say why.
    echo.
    pause
    exit /b 1
)

echo.
echo  Starting...
echo.
dotnet run -c Debug --no-build
if errorlevel 1 (
    echo.
    echo  The app closed with an error.
    pause
)
