@echo off
setlocal
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Or-Reinstall.ps1"
set "INSTALL_EXIT_CODE=%ERRORLEVEL%"
echo.
if not "%INSTALL_EXIT_CODE%"=="0" echo 安装未完成，请查看上方错误信息。
pause
exit /b %INSTALL_EXIT_CODE%
