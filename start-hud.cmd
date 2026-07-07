@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "PROJECT_FILE=%SCRIPT_DIR%src\YiboCodexHUD.Desktop\YiboCodexHUD.Desktop.csproj"
set "EXE_PATH=%SCRIPT_DIR%src\YiboCodexHUD.Desktop\bin\Debug\net8.0-windows\YiboCodexHUD.Desktop.exe"

pushd "%SCRIPT_DIR%"

echo [start-hud] Stopping existing YiboCodexHUD.Desktop process...
taskkill /F /IM YiboCodexHUD.Desktop.exe >nul 2>nul
timeout /t 1 /nobreak >nul

echo [start-hud] Building project...
dotnet build "%PROJECT_FILE%" >nul
if errorlevel 1 (
    echo [start-hud] Build failed.
    popd
    pause
    exit /b 1
)

if not exist "%EXE_PATH%" (
    echo [start-hud] Executable still not found:
    echo %EXE_PATH%
    popd
    pause
    exit /b 1
)

echo [start-hud] Launching YiboCodexHUD...
start "" "%EXE_PATH%"
set "EXIT_CODE=%ERRORLEVEL%"

popd
exit /b %EXIT_CODE%
