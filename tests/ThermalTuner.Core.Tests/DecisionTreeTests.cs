using ThermalTuner.Core;
using ThermalTuner.Core.DecisionTree;
using ThermalTuner.Core.DecisionTree.Nodes;
using ThermalTuner.Core.Taxonomy;

namespace ThermalTuner.Core.Tests;

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
        Test_LightLoad_AnchorsToFirmware_WhenDecisionWasRatchetUp();
        Test_LightLoad_FallsBackToDecision_WhenFirmwareUnknown();
        Test_Controller_NoRatchet_AfterLoadDrops();
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

    public void Test_LightLoad_AnchorsToFirmware_WhenDecisionWasRatchetUp()
    {
        var profile = UserProfile.Balanced(_hw);
        var snapshot = new ThermalSnapshot
        {
            CurrentTempC = 48.0,
            CurrentPL1W = 65.0,      // 上一拍被高负载推到平台上限
            FirmwarePL1W = 45.0,     // 固件实读值
            CurrentFanPercent = 50.0,
            LoadPercent = 7.0,
            PackagePowerW = 10.0,
            IsProchotActive = false
        };

        var receipt = _engine.EvaluateTick(snapshot, profile, _gains);

        Assert(receipt.TriggeredRuleName == "LightLoadFilter", "Rule must be LightLoadFilter");
        Assert(Math.Abs(receipt.Action.TargetPL1W - 45.0) < 1e-6,
            $"轻载必须回落到固件实值 45W，不能锁在决策值 65W（实得 {receipt.Action.TargetPL1W:F1}W）");
    }

    public void Test_LightLoad_FallsBackToDecision_WhenFirmwareUnknown()
    {
        var profile = UserProfile.Balanced(_hw);
        var snapshot = new ThermalSnapshot
        {
            CurrentTempC = 55.0,
            CurrentPL1W = 35.0,
            FirmwarePL1W = double.NaN,   // 无 MSR 通道
            LoadPercent = 10.0,
            PackagePowerW = 8.0
        };

        var receipt = _engine.EvaluateTick(snapshot, profile, _gains);
        Assert(Math.Abs(receipt.Action.TargetPL1W - 35.0) < 1e-6,
            "固件值不可读时应回退到决策值，保持向后兼容");
    }

    public void Test_Controller_NoRatchet_AfterLoadDrops()
    {
        var c = new AutoCpuController(HardwareLimits.DefaultIntelMobile);
        // 高负载连续升档，把内部 _pl1 推高
        for (int i = 0; i < 10; i++)
            c.Update(tempC: 60, powerW: 40, ceilingC: 92, ambientC: 25, profile: "performance",
                loadPct: 95, dTdt: 0.2, perf: new PerfSample { CoreClocksGhz = 3.0, MaxTurboGhz = 3.8 });

        // 负载掉回轻载：必须回落，不能永久锁在高位
        var dec = c.Update(tempC: 48, powerW: 10, ceilingC: 88, ambientC: 25, profile: "balanced",
            loadPct: 7, dTdt: 0.0, firmwarePl1W: 45.0);

        Assert(Math.Abs(dec.Pl1W - 45.0) < 1e-6,
            $"轻载后应回落到固件 45W，实得 {dec.Pl1W:F1}W（棘轮未消除）");
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
