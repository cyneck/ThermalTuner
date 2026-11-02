using ThermalPilot.Core;
using ThermalPilot.Core.DecisionTree;
using ThermalPilot.Core.DecisionTree.Nodes;
using ThermalPilot.Core.Taxonomy;

namespace ThermalPilot.Core.Tests;

/// <summary>
/// 决策树因果逻辑与防呆约束单元测试集
/// 脱离物理硬件模拟各级工况，确保决策链因果清晰、行为可预测。
/// </summary>
public class DecisionTreeTests
{
    private readonly HardwareLimits _hw = HardwareLimits.DefaultIntelMobile;
    private readonly TuningGains _gains = TuningGains.Default;
    private readonly ThermalDecisionEngine _engine = new();

    public void RunAllTests()
    {
        Test_SafetyWatchdog_TriggersOnProchot();
        Test_SafetyWatchdog_TriggersOnNearTjMax();
        Test_LightLoad_PreservesCurrentState();
        Test_CeilingExceeded_StepsDownPower();
        Test_ThermalPredictive_PreventsOvershoot();
        Test_BottleneckArbiter_StepsUpPower_WhenHeadroomAvailable();
        Test_BottleneckArbiter_HoldsPower_WhenFrequencyCap();
        Test_ParamConstraints_EnforcesInvariants();
        Console.WriteLine("All DecisionTree unit tests passed successfully!");
    }

    public void Test_SafetyWatchdog_TriggersOnProchot()
    {
        var profile = UserProfile.Performance(_hw);
        var snapshot = new ThermalSnapshot
        {
            CurrentTempC = 80.0,
            CurrentPL1W = 45.0,
            CurrentFanPercent = 60.0,
            IsProchotActive = true
        };

        var receipt = _engine.EvaluateTick(snapshot, profile, _gains);

        Assert(receipt.TriggeredRuleName == "SafetyWatchdog", "Rule must be SafetyWatchdog");
        Assert(receipt.Action.Direction == -1, "Direction must be downward");
        Assert(receipt.Action.TargetFanPercent == 100.0, "Fan must be 100%");
        Assert(receipt.Action.ForceImmediateApply, "Must force immediate apply");
    }

    public void Test_SafetyWatchdog_TriggersOnNearTjMax()
    {
        var profile = UserProfile.Performance(_hw);
        var snapshot = new ThermalSnapshot
        {
            CurrentTempC = 98.0, // TjMax is 100
            CurrentPL1W = 45.0,
            CurrentFanPercent = 80.0,
            IsProchotActive = false
        };

        var receipt = _engine.EvaluateTick(snapshot, profile, _gains);

        Assert(receipt.TriggeredRuleName == "SafetyWatchdog", "Rule must be SafetyWatchdog on extreme temp");
        Assert(receipt.Action.TargetPL1W <= snapshot.CurrentPL1W - _gains.EmergencyStepDownWatts, "Must drop by emergency step");
    }

    public void Test_LightLoad_PreservesCurrentState()
    {
        var profile = UserProfile.Balanced(_hw);
        var snapshot = new ThermalSnapshot
        {
            CurrentTempC = 55.0,
            CurrentPL1W = 35.0,
            LoadPercent = 10.0, // Light load
            PackagePowerW = 8.0,
            IsProchotActive = false
        };

        var receipt = _engine.EvaluateTick(snapshot, profile, _gains);

        Assert(receipt.TriggeredRuleName == "LightLoadFilter", "Rule must be LightLoadFilter");
        Assert(receipt.Action.Direction == 0, "Direction must be 0 (hold)");
        Assert(receipt.Action.TargetPL1W == 35.0, "PL1 must remain unchanged");
    }

    public void Test_CeilingExceeded_StepsDownPower()
    {
        var profile = UserProfile.Silent(_hw); // Ceiling is 85.0
        var snapshot = new ThermalSnapshot
        {
            CurrentTempC = 87.0, // Exceeds 85.0
            CurrentPL1W = 35.0,
            LoadPercent = 70.0,
            PackagePowerW = 32.0,
            IsProchotActive = false
        };

        var receipt = _engine.EvaluateTick(snapshot, profile, _gains);

        Assert(receipt.TriggeredRuleName == "CeilingExceededFeedback", "Rule must be CeilingExceededFeedback");
        Assert(receipt.Action.Direction == -1, "Direction must be -1");
        Assert(receipt.Action.TargetPL1W == 35.0 - _gains.StepDownWatts, "PL1 must step down by StepDownWatts");
    }

    public void Test_ThermalPredictive_PreventsOvershoot()
    {
        var profile = UserProfile.Balanced(_hw); // Ceiling is 88.0
        var snapshot = new ThermalSnapshot
        {
            CurrentTempC = 86.0,
            CurrentPL1W = 35.0,
            PackagePowerW = 35.0,
            LoadPercent = 80.0,
            DtDt = 0.2, // Quasi-steady
            IsProchotActive = false,
            ThermalModel = new IdentifiedThermalState
            {
                EstimatedRth = 1.0, // 1 K/W. Adding +3W will cause +3°C -> 89°C > 88°C
                IsConfident = true
            }
        };

        var receipt = _engine.EvaluateTick(snapshot, profile, _gains);

        Assert(receipt.TriggeredRuleName == "ThermalPredictiveFeasibility", "Rule must be ThermalPredictiveFeasibility");
        Assert(receipt.Action.Direction == 0, "Must prevent stepping up and hold power");
        Assert(receipt.Action.TargetPL1W == 35.0, "PL1 must be held");
    }

    public void Test_BottleneckArbiter_StepsUpPower_WhenHeadroomAvailable()
    {
        var profile = UserProfile.Performance(_hw); // Ceiling is 92.0
        var snapshot = new ThermalSnapshot
        {
            CurrentTempC = 70.0, // Headroom is 22°C!
            CurrentPL1W = 35.0,
            PackagePowerW = 34.5,
            LoadPercent = 95.0,
            DtDt = 0.5,
            CoreClockGhz = 3.2,
            MaxTurboGhz = 3.8, // Not at cap
            IsProchotActive = false,
            ThermalModel = new IdentifiedThermalState
            {
                EstimatedRth = 0.5, // 0.5 K/W. +3W will only increase 1.5°C -> 71.5°C << 92°C
                IsConfident = true
            }
        };

        var receipt = _engine.EvaluateTick(snapshot, profile, _gains);

        Assert(receipt.TriggeredRuleName == "BottleneckArbiter", "Rule must be BottleneckArbiter");
        Assert(receipt.Action.Direction == 1, "Direction must be +1 (boost)");
        Assert(receipt.Action.TargetPL1W == 35.0 + _gains.StepUpWatts, "PL1 must step up");
    }

    public void Test_BottleneckArbiter_HoldsPower_WhenFrequencyCap()
    {
        var profile = UserProfile.Performance(_hw); // Ceiling is 92.0
        var snapshot = new ThermalSnapshot
        {
            CurrentTempC = 75.0,
            CurrentPL1W = 35.0,
            PackagePowerW = 34.0,
            LoadPercent = 80.0,
            CoreClockGhz = 3.8,
            MaxTurboGhz = 3.8, // Reached max turbo
            IsProchotActive = false
        };

        var receipt = _engine.EvaluateTick(snapshot, profile, _gains);

        Assert(receipt.TriggeredRuleName == "BottleneckArbiter", "Rule must be BottleneckArbiter");
        Assert(receipt.ActiveLimit == BindingLimit.FrequencyCap, "Active limit must be FrequencyCap");
        Assert(receipt.Action.Direction == 0, "Direction must be 0 (hold)");
    }

    public void Test_ParamConstraints_EnforcesInvariants()
    {
        var violations = ParamConstraints.Validate(pl1: 45.0, pl2: 30.0, tau: 16.0, epp: 128, ceiling: 88.0);
        Assert(violations.Any(v => v.Limit == BindingLimit.Pl2LessPl1), "Must detect PL2 < PL1 violation");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"Assertion Failed: {message}");
    }
}
