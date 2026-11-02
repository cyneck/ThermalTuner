@echo off
rem ============================================================
rem  ThermalPilot - 10 minute quick test (double-click friendly)
rem  ASCII ONLY: cmd.exe mis-parses UTF-8 batch files.
rem  Real logic lives in run-lab.ps1 (UTF-8 with BOM).
rem ============================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-lab.ps1" quick10 %*
if errorlevel 1 (
    echo.
    echo [launcher] run-lab.ps1 exited with an error.
    pause
)
