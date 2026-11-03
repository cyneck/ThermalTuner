using System.Diagnostics;
using ThermalPilot.App.Services.Msr;
using ThermalPilot.Core.Environment;

namespace ThermalPilot.App.Services;

/// <summary>
/// RAPL 能量计数器功耗计量（只读，不写任何 MSR）。
/// 原理：MSR 0x611（PKG_ENERGY_STATUS）是一个 32 位、会回绕的能量累加器，
/// 相邻两拍取差值 ÷ 时间 = 真实包功耗，不依赖 LHM 驱动。
///   · 能量单位来自 0x606 的 ESU 位段：1 / 2^ESU 焦耳（默认 2^-16）
///   · AMD 平台对应 0xC001029B / 0xC0010299
/// 任一环节不可用即降级为「无功耗读数」，绝不返回猜出来的数值。
/// </summary>
public sealed class RaplPowerMeter
{
    private const uint IntelPowerUnitMsr = 0x606;
    private const uint IntelPkgEnergyMsr = 0x611;
    private const uint AmdPowerUnitMsr = 0xC0010299;
    private const uint AmdPkgEnergyMsr = 0xC001029B;

    private const double FallbackEnergyUnitJ = 1.0 / 65536.0;
    private const double MinPlausibleW = 1.0;
    private const double MaxPlausibleW = 400.0;
    private const long MinGapMs = 150;
    private const double Smoothing = 0.5;

    private readonly MsrChannel _channel;
    private readonly uint _energyMsr;
    private readonly double _energyUnitJ;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private uint _prevRaw;
    private long _prevMs;
    private int _frozenStreak;
    private double _lastW = double.NaN;

    public RaplPowerMeter(MsrChannel channel, CpuVendor vendor)
    {
        _channel = channel;

        bool amd = vendor == CpuVendor.Amd;
        _energyMsr = amd ? AmdPkgEnergyMsr : IntelPkgEnergyMsr;
        uint unitMsr = amd ? AmdPowerUnitMsr : IntelPowerUnitMsr;

        _energyUnitJ = FallbackEnergyUnitJ;
        if (_channel.TryRead(unitMsr, out ulong unitRaw))
        {
            int esu = (int)((unitRaw >> 8) & 0x1F);
            if (esu is > 0 and < 32)
                _energyUnitJ = 1.0 / (1L << esu);
        }

        if (!_channel.TryRead(_energyMsr, out ulong energy))
        {
            Available = false;
            Detail = $"读取 RAPL 能量计数器 0x{_energyMsr:X} 失败（可能被虚拟化屏蔽）";
            return;
        }

        _prevRaw = (uint)(energy & 0xFFFFFFFF);
        _prevMs = _clock.ElapsedMilliseconds;
        Available = true;
        Detail = $"RAPL 能量计数器就绪（0x{_energyMsr:X}，单位 {_energyUnitJ * 1e6:F2} µJ）";
    }

    /// <summary>计量是否能给出可信读数。</summary>
    public bool Available { get; private set; }

    public string Detail { get; private set; } = "未探测";

    public double LastW => _lastW;

    /// <summary>取一次功耗；首拍只做基线登记，返回 false。</summary>
    public bool TrySample(out double watts)
    {
        watts = double.NaN;
        if (!Available) return false;

        if (!_channel.TryRead(_energyMsr, out ulong raw))
        {
            Available = false;
            Detail = "能量计数器读取中断";
            return false;
        }

        uint current = (uint)(raw & 0xFFFFFFFF);
        long now = _clock.ElapsedMilliseconds;
        long gapMs = now - _prevMs;

        if (gapMs < MinGapMs)
        {
            watts = _lastW;
            return !double.IsNaN(_lastW);
        }

        // 32 位计数器回绕：cur < prev 时补满一圈
        uint delta = current >= _prevRaw
            ? current - _prevRaw
            : (uint)(0x1_0000_0000UL - _prevRaw + current);

        double seconds = gapMs / 1000.0;
        double instant = delta * _energyUnitJ / seconds;

        _prevRaw = current;
        _prevMs = now;

        if (instant < MinPlausibleW)
        {
            // 计数器长期不动说明当前平台不提供真实能量读数，主动停用而不是给 0W
            if (++_frozenStreak >= 3)
            {
                Available = false;
                Detail = "能量计数器读数不变化（平台不提供真实功耗）";
            }
            return false;
        }

        if (instant > MaxPlausibleW) return false;

        _frozenStreak = 0;
        _lastW = double.IsNaN(_lastW) ? instant : _lastW * (1 - Smoothing) + instant * Smoothing;
        watts = _lastW;
        return true;
    }
}
