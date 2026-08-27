@echo off
rem Emmanim Lag Fix - one-click installer.
rem Supplies the execution policy that an unsigned downloaded script lacks,
rem then keeps the window open so the result stays readable.
setlocal
title Emmanim Lag Fix - Installer
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
if not "%ERRORLEVEL%"=="0" (
    echo.
    echo Installation failed. Read the message above.
)
echo.
pause
