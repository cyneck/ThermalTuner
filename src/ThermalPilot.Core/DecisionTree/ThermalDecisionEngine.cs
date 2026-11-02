using ThermalPilot.Core.DecisionTree.Nodes;
using ThermalPilot.Core.Taxonomy;

namespace ThermalPilot.Core.DecisionTree;

/// <summary>
/// 决策树控制主引擎（Thermal Decision Engine）
/// 负责以组合模式调度节点，按确定性顺序短路求值，输出完整因果追踪审计。
/// </summary>
public class ThermalDecisionEngine
{
    private readonly List<IDecisionNode> _nodes;

    public IReadOnlyList<IDecisionNode> Nodes => _nodes;

    public ThermalDecisionEngine()
    {
        // 严格遵循因果优先级的流水线装配：
        // 1. 安全熔断兜底 -> 2. 轻载节能防扰动 -> 3. 超温负反馈抑制 -> 4. 在线热阻超调预警 -> 5. 性能瓶颈仲裁
        _nodes = new List<IDecisionNode>
        {
            new SafetyWatchdogNode(),
            new LightLoadFilterNode(),
            new CeilingExceededNode(),
            new ThermalPredictiveNode(),
            new BottleneckArbiterNode()
        };
    }

    public ThermalDecisionEngine(IEnumerable<IDecisionNode> customNodes)
    {
        _nodes = customNodes.ToList();
    }

    public DecisionReceipt EvaluateTick(ThermalSnapshot snapshot, UserProfile profile, TuningGains gains)
    {
        var auditPath = new List<string>();

        foreach (var node in _nodes)
        {
            var result = node.Evaluate(snapshot, profile, gains, auditPath);
            if (result.IsMatch && result.Receipt != null)
            {
                return result.Receipt;
            }
        }

        // 默认保底兜底（理论不可达，因为 BottleneckArbiterNode 包含默认分支）
        return new DecisionReceipt
        {
            TriggeredRuleName = "DefaultFallback",
            CausalReason = "未命中任何特定规则，维持当前硬件基准配置",
            ActiveLimit = BindingLimit.Unknown,
            Action = new ControllerAction
            {
                TargetPL1W = snapshot.CurrentPL1W,
                TargetPL2W = snapshot.CurrentPL1W * profile.Pl2Ratio,
                TargetTauS = profile.TauSeconds,
                TargetEpp = profile.Epp,
                TargetFanPercent = snapshot.CurrentFanPercent,
                Direction = 0,
                ForceImmediateApply = false
            },
            EvaluatedPath = auditPath.ToArray()
        };
    }
}
