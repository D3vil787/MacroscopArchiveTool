@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem Drag two or more .json reports (one per server) onto this file.
if "%~1"=="" (echo Drag two or more .json reports onto this file. & pause & exit /b 1)
mat.exe merge %* --open
echo.
pause
