@echo off
rem Emmanim Lag Fix - rules-only installer.
rem For users of another mod loader (e.g. Yet Another Mod Loader): skips the
rem winmm.dll/ModLoader.dll payload entirely, so it can coexist with whatever
rem already owns the winmm.dll proxy slot in Cosmoteer\Bin. The other loader
rem may still pick up this mod's code modules from its folder on its own.
setlocal
title Emmanim Lag Fix - Installer (no code loader)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -NoLoader %*
if not "%ERRORLEVEL%"=="0" (
    echo.
    echo Installation failed. Read the message above.
)
echo.
pause
