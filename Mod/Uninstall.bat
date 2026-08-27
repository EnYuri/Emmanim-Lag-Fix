@echo off
rem Emmanim Lag Fix - uninstaller.
rem Removes only files whose hashes still match this mod install record.
setlocal
title Emmanim Lag Fix - Uninstaller
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall.ps1" %*
if not "%ERRORLEVEL%"=="0" (
    echo.
    echo Uninstall failed. Read the message above.
)
echo.
pause
