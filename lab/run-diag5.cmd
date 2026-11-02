@echo off
rem ============================================================
rem  ThermalPilot - 5.3 min THERMAL CHAIN DIAGNOSIS (double-click)
rem  Localizes which heat-transfer link is broken (TIM / fan /
rem  overall / power wall) and feeds tools/lab_diagnose.py.
rem  ASCII ONLY: cmd.exe mis-parses UTF-8 batch files.
rem ============================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-plan.ps1" -Plan "lab\plan-diag5-v1.json" -Tag diag5
if errorlevel 1 (
    echo.
    echo [launcher] run-plan.ps1 exited with an error.
    pause
)
