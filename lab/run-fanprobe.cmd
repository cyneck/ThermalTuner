@echo off
rem ============================================================
rem  ThermalPilot - 9.5 min FAN LEVER probe (double-click friendly)
rem  Same power (~35W, cap is proven ineffective), fan 100% vs 60%.
rem  ASCII ONLY: cmd.exe mis-parses UTF-8 batch files.
rem ============================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-plan.ps1" -Plan "lab\plan-fanprobe-v1.json" -Tag fan1
if errorlevel 1 (
    echo.
    echo [launcher] run-plan.ps1 exited with an error.
    pause
)
