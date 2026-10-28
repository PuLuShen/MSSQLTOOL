@echo off
setlocal
cd /d "%~dp0.."
title MSSQL Tool - publish GitHub release

echo Manual release upload: nothing is uploaded until this window is started.
echo Version and notes come from MSSQLTool\source.extension.vsixmanifest and RELEASE_NOTES.md.
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish-GitHubRelease.ps1" %*
set "exitCode=%ERRORLEVEL%"

echo.
if not "%exitCode%"=="0" echo FAILED: exit code %exitCode%.
echo Press any key to close this window.
pause >nul
exit /b %exitCode%
