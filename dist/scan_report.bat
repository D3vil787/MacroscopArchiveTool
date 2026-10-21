@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem Read-only scan of this server archive, then open the HTML report.
mat.exe scan --open
echo.
pause
