@echo off
setlocal EnableExtensions

set "APP_NAME=YiboCodexHUD"
set "INSTALL_DIR=%LOCALAPPDATA%\Programs\%APP_NAME%"
set "START_MENU_DIR=%APPDATA%\Microsoft\Windows\Start Menu\Programs\%APP_NAME%"
set "DESKTOP_SHORTCUT=%USERPROFILE%\Desktop\%APP_NAME%.lnk"
set "SOURCE_DIR=%~dp0"
set "EXE_NAME=YiboCodexHUD.Desktop.exe"

if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"
if not exist "%START_MENU_DIR%" mkdir "%START_MENU_DIR%"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$src = [System.IO.Path]::GetFullPath($env:SOURCE_DIR);" ^
  "$dest = [System.IO.Path]::GetFullPath($env:INSTALL_DIR);" ^
  "$exclude = @('install.cmd');" ^
  "Get-ChildItem -LiteralPath $src -File | Where-Object { $_.Name -notin $exclude } | Copy-Item -Destination $dest -Force"
if errorlevel 1 goto :error

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$shell = New-Object -ComObject WScript.Shell;" ^
  "$exePath = Join-Path $env:INSTALL_DIR $env:EXE_NAME;" ^
  "$desktop = $shell.CreateShortcut($env:DESKTOP_SHORTCUT);" ^
  "$desktop.TargetPath = $exePath;" ^
  "$desktop.WorkingDirectory = $env:INSTALL_DIR;" ^
  "$desktop.IconLocation = $exePath;" ^
  "$desktop.Save();" ^
  "$menuPath = Join-Path $env:START_MENU_DIR ($env:APP_NAME + '.lnk');" ^
  "$menu = $shell.CreateShortcut($menuPath);" ^
  "$menu.TargetPath = $exePath;" ^
  "$menu.WorkingDirectory = $env:INSTALL_DIR;" ^
  "$menu.IconLocation = $exePath;" ^
  "$menu.Save();"
if errorlevel 1 goto :error

start "" "%INSTALL_DIR%\%EXE_NAME%"
exit /b 0

:error
echo Install failed.
pause
exit /b 1
