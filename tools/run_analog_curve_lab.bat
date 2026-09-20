@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0.."
py "%~dp0analog_curve_lab.py"
if errorlevel 1 (
    echo.
    echo Nao foi possivel executar o laboratorio de curvas.
    pause
)
