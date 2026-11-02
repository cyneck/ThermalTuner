using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThermalPilot.Core;

/// <summary>
/// 实验标定产出的「策略模型」——由 tools/lab_model.py 训练导出，C# 侧只做**推理**。
///
/// 设计取舍：
///   · 训练放 Python（快速迭代、可视化方便），运行时用 C# 纯推理，不引入 Python 依赖；
///   · 模型文件是 JSON，含稳态模型 + 动学参数（τ）+ 反解阶梯表，便于版本化、可回滚；
///   · 支持四种稳态模型：线性基函数 / 均值 / 阶梯表（固件查表）/ MLP（神经网络）。
/// </summary>
public sealed class PolicyModel
{
    public sealed class Term
    {
        public string Name { get; set; } = "";
        public double Coef { get; set; }
    }

    public sealed class Steady
    {
        public string Type { get; set; } = "";
        // linear_basis
        public double Intercept { get; set; }
        public List<double> Mu { get; set; } = new();
        public List<double> Sd { get; set; } = new();
        public List<Term> Terms { get; set; } = new();
        // mean
        public double Value { get; set; }
        // mlp
        public int Hidden { get; set; }
        public double Ymu { get; set; }
        public double B2 { get; set; }
        public List<List<double>> W1 { get; set; } = new();
        public List<double> B1 { get; set; } = new();
        public List<double> W2 { get; set; } = new();
        // stepwise
        public List<List<double?>> Bins { get; set; } = new();
    }

    public sealed class DynamicInfo
    {
        [JsonPropertyName("tau_s")] public double TauS { get; set; }
        public double Alpha { get; set; }
    }

    public sealed class LadderOption
    {
        public string Mode { get; set; } = "";
        public int Cap { get; set; }
        public double? Fan { get; set; }
        public int F { get; set; }
        public double P { get; set; }
        [JsonPropertyName("pred_T")] public double PredT { get; set; }
        [JsonPropertyName("spl_delta_db")] public double SplDeltaDb { get; set; }
    }

    public sealed class LadderRow
    {
        [JsonPropertyName("T_hi")] public double THi { get; set; }
        public bool Achievable { get; set; }
        public List<LadderOption> Options { get; set; } = new();
    }

    public int Version { get; set; }
    public string Mode { get; set; } = "bios";
    [JsonPropertyName("fan_controllable")] public bool FanControllable { get; set; }
    [JsonPropertyName("steady_model")] public Steady SteadyModel { get; set; } = new();
    public DynamicInfo Dynamic { get; set; } = new();
    [JsonPropertyName("control_ladder")] public List<LadderRow> ControlLadder { get; set; } = new();
    [JsonPropertyName("source_csv")] public string SourceCsv { get; set; } = "";

    [JsonIgnore] public bool HasFan => FanControllable;

    private static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static PolicyModel? Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var m = JsonSerializer.Deserialize<PolicyModel>(json, Opt);
            if (m == null) return null;
            Log.Info($"已加载策略模型：{Path.GetFileName(path)}（{m.Mode}，{m.ControlLadder.Count} 档）");
            return m;
        }
        catch (Exception ex)
        {
            Log.Error("策略模型加载失败：" + path, ex);
            return null;
        }
    }

    /// <summary>预测稳态温度。fanPct 只用风扇可控时作输入（否则忽略）。</summary>
    public double PredictSteady(double powerW, double fanPct = 60)
    {
        var s = SteadyModel;
        switch ((s.Type ?? "").ToLowerInvariant())
        {
            case "mean":
                return s.Value;

            case "linear_basis":
            {
                double f01 = fanPct / 100.0;
                double sum = s.Intercept;
                var raw = s.Terms.Select(t => EvalTerm(t.Name, powerW, f01)).ToList();
                for (int i = 0; i < s.Terms.Count && i < raw.Count; i++)
                {
                    double sd = i < s.Sd.Count && Math.Abs(s.Sd[i]) > 1e-9 ? s.Sd[i] : 1.0;
                    double mu = i < s.Mu.Count ? s.Mu[i] : 0;
                    sum += s.Terms[i].Coef * ((raw[i] - mu) / sd);
                }
                return sum;
            }

            case "mlp":
            {
                double f01 = fanPct / 100.0;
                var x = HasFan ? new[] { powerW, f01 } : new[] { powerW };
                double outp = s.B2 + s.Ymu;
                for (int k = 0; k < s.Hidden; k++)
                {
                    double z = s.B1.Count > k ? s.B1[k] : 0;
                    for (int i = 0; i < x.Length && i < s.W1[k].Count; i++)
                    {
                        double sd = i < s.Sd.Count && Math.Abs(s.Sd[i]) > 1e-9 ? s.Sd[i] : 1.0;
                        double mu = i < s.Mu.Count ? s.Mu[i] : 0;
                        z += s.W1[k][i] * ((x[i] - mu) / sd);
                    }
                    outp += (s.W2.Count > k ? s.W2[k] : 0) * Math.Tanh(z);
                }
                return outp;
            }

            case "stepwise":
            {
                foreach (var b in s.Bins)
                {
                    double lo = b.Count > 0 && b[0].HasValue ? b[0]!.Value : 0;
                    double hi = b.Count > 1 && b[1].HasValue ? b[1]!.Value : double.MaxValue;
                    if (powerW >= lo && powerW < hi) return b[2] ?? 0;
                }
                return s.Bins.Count > 0 ? (s.Bins[^1][2] ?? 0) : 0;
            }

            default:
                return 0;
        }
    }

    private static double EvalTerm(string name, double p, double f) => name switch
    {
        "1" => 1.0,
        "P" => p,
        "P²" => p * p,
        "P·fan" => p * f,
        "P·fan²" => p * f * f,
        "fan" => f,
        "fan²" => f * f,
        _ => 0.0
    };

    public enum Strategy { Power, Balanced, Quiet }

    /// <summary>
    /// 按目标温度取一档策略。
    /// 实现上直接在阶梯表里找"最严格但可达"的一档，避免运行时再做反解（省 CPU，也保证与报告一致）。
    /// </summary>
    public LadderOption? Resolve(double targetTemp, Strategy strategy)
    {
        string want = strategy switch
        {
            Strategy.Power => "性能优先",
            Strategy.Quiet => "静音优先",
            _ => "折中"
        };

        var row = ControlLadder
            .Where(r => r.Achievable && r.THi >= targetTemp - 0.01)
            .OrderBy(r => r.THi)
            .FirstOrDefault();
        if (row == null) return null;

        var opt = row.Options.FirstOrDefault(o => o.Mode == want) ?? row.Options.FirstOrDefault();
        return opt;
    }

    /// <summary>一阶热动态推进一步（给控制器做超前/预测用）。</summary>
    public double StepDynamic(double current, double steady, double dtSec)
    {
        double alpha = Dynamic.TauS > 1 ? 1 - Math.Exp(-dtSec / Dynamic.TauS) : Dynamic.Alpha;
        return current + (steady - current) * Math.Clamp(alpha, 0, 1);
    }
}
