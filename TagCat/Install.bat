@echo off
title TagCat - Install
cd /d "%~dp0"

if not exist "app\TagCat.exe" (
    echo.
    echo  Could not find app\TagCat.exe next to this file.
    echo  Unzip the whole installer package and run Install.bat from inside it.
    echo.
    pause
    exit /b 1
)

echo.
echo  TagCat - First-time setup
echo  ================================
echo.
echo  Checking for what this needs to run (.NET Runtime, VLC) and fetching
echo  anything missing. This only happens once - after this, just run
echo  app\TagCat.exe directly, or Install.bat again, either works.
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
if errorlevel 1 (
    echo.
    echo  Setup did not finish cleanly. See the messages above.
    pause
    exit /b 1
)

echo.
echo  Setup complete. Starting TagCat...
echo.
start "" "%~dp0app\TagCat.exe"
