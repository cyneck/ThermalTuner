using System.Management;

namespace ThermalPilot.Core;

/// <summary>
/// 机型探测 + 风扇控制方案建议。
///
/// 为什么需要它：笔记本风扇挂在 EC（嵌入式控制器）上，没有通用接口，各家私有寄存器也不同。
/// 能真正控制风扇的只有"针对该机型写了配置"的社区项目，因此必须按机型号给出精确建议，
/// 而不是笼统地说"不支持"。
/// </summary>
public static class MachineProbe
{
    public static string Manufacturer { get; private set; } = "";
    public static string Model { get; private set; } = "";
    public static bool IsLaptop { get; private set; }
    private static bool _loaded;

    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in searcher.Get())
            {
                Manufacturer = (mo["Manufacturer"] as string ?? "").Trim();
                Model = (mo["Model"] as string ?? "").Trim();
                break;
            }
        }
        catch { }

        try
        {
            using var bat = new ManagementObjectSearcher("SELECT Name FROM Win32_Battery");
            IsLaptop = bat.Get().Count > 0;
        }
        catch { }
    }

    public static string Summary
    {
        get { Load(); return $"{Manufacturer} {Model}（{(IsLaptop ? "笔记本" : "台式机")}）".Trim(); }
    }

    /// <summary>按机型给出风扇控制的可执行建议。</summary>
    public static (string detail, string advice, string? url) FanAdvice(bool anyLhmChannel)
    {
        Load();
        string m = (Manufacturer + " " + Model).ToUpperInvariant();

        if (anyLhmChannel)
            return ("已有可用的 PWM 调速通道，无需第三方工具。", "", null);

        if (!IsLaptop)
            return ("台式机主板未暴露 PWM 通道。",
                "台式机请优先在 BIOS 里把对应风扇接口设为 PWM 模式（部分主板默认 DC 模式不报转速），" +
                "并确认超级 I/O 芯片（Nuvoton/ITE/Winbond）在支持列表内。",
                null);

        if (m.Contains("HP") && (m.Contains("OMEN") || m.Contains("PAVILION GAMING") || m.Contains("VICTUS")))
            return ("惠普 OMEN/暗影精灵系列：风扇由 EC 私有固件控制，LibreHardwareMonitor 无此机型配置，读不到转速也控不了速。",
                "可用 NoteBook FanControl（NBFC）接管：安装 NBFC 1.6.3 → 在配置列表里选「HP OMEN Notebook PC 15」" +
                "（社区验证过 OMEN 15/17 通用）→ 应用后风扇转速与占空比即可读可写。" +
                "装好并选好配置后，回到本程序点「环境检测」，本程序会自动通过 nbfc CLI 接管（无需再改配置）。",
                "https://github.com/hirschmann/nbfc/releases");

        if (m.Contains("LENOVO") || m.Contains("THINKPAD"))
            return ("联想机型：EC 私有控制，LHM 无配置。",
                "ThinkPad 可用 NBFC（部分机型）+ Lenovo Legion Toolkit（拯救者系列，自带风扇曲线与转速）；" +
                "装好后本程序可接管。", "https://github.com/hirschmann/nbfc/releases");

        if (m.Contains("ASUS") || m.Contains("ROG") || m.Contains("TUF"))
            return ("华硕机型：EC 私有控制，LHM 无配置。",
                "可试用 NBFC 对应机型配置，或 ASUS Armoury Crate 的「手动风扇曲线」（部分机型支持）。",
                "https://github.com/hirschmann/nbfc/releases");

        if (m.Contains("MSI") || m.Contains("MICRO-STAR"))
            return ("微星机型：EC 私有控制，LHM 无配置。",
                "MSI Center / Dragon Center 可手动设风扇曲线；NBFC 亦有部分机型配置。",
                "https://github.com/hirschmann/nbfc/releases");

        if (m.Contains("DELL") || m.Contains("ALIENWARE"))
            return ("戴尔/外星人机型：EC 私有控制。",
                "Alienware Command Center 支持风扇曲线；Dell 商务本可用 DellFanCmd。",
                "https://github.com/hirschmann/nbfc/releases");

        return ("本机为笔记本，风扇由 EC 私有固件管理，通用接口读不到转速。",
            "可尝试 NoteBook FanControl（NBFC）：安装后在配置列表里找与你机型最接近的条目，" +
            "应用后若能读到转速即可用；装好后本程序会自动接管。",
            "https://github.com/hirschmann/nbfc/releases");
    }
}
