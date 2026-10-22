@echo off
rem Launch the Debug build of DeskCanvas against the real data folder.
rem
rem Keep this file pure ASCII: cmd.exe reads .cmd files in the OEM code page (GBK
rem here), so UTF-8 Chinese comments get decoded as garbage and run as commands.
rem
rem Why the env var: when a Widgets folder sits next to the exe the app applies the
rem portable dev rule and defaults to %LOCALAPPDATA%\DeskCanvas-dev - where the
rem launcher is disabled and the widget layout differs. Point it at the real folder
rem so the two summon gestures and your own desktop show up.

set "DESKCANVAS_DATA_FOLDER=%LOCALAPPDATA%\DeskCanvas"

cd /d "%~dp0src\DeskCanvas\bin\Debug\net10.0"
if not exist DeskCanvas.exe (
    echo DeskCanvas.exe not found. Build it first:
    echo     dotnet build src\DeskCanvas\DeskCanvas.csproj -c Debug
    pause
    exit /b 1
)

start "" DeskCanvas.exe
