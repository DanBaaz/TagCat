@echo off
rem Convenience launcher. The real scripts live in the TagCat folder alongside the
rem source; this just saves opening it. CLEAN-REBUILD.bat and MAKE-EXE.bat are in there too.
cd /d "%~dp0"

if not exist "TagCat\RUN.bat" (
    echo.
    echo  Could not find TagCat\RUN.bat next to this file.
    echo  Unzip the whole archive and keep this launcher beside the TagCat folder.
    echo.
    pause
    exit /b 1
)

call "TagCat\RUN.bat"
