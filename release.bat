@echo off
setlocal
chcp 65001 >nul
where pwsh >nul 2>&1
if %errorlevel% equ 0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0.github\scripts\release.ps1"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0.github\scripts\release.ps1"
)
echo.
pause
