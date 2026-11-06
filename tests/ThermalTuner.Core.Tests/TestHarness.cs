namespace ThermalTuner.Core.Tests;

/// <summary>
/// 轻量断言与计数（不引入外部测试框架，保持 zero-dependency 的独立测试入口）。
/// 新增功能测试统一用 Check：单个断言失败不会中断同套件其余用例，
/// 最终由 Program 汇总失败清单并返回非零退出码。
/// </summary>
public static class Check
{
    public static int Passed { get; private set; }
    public static List<string> Failures { get; } = new();

    private static string _suite = "未命名";

    public static void Suite(string name) => _suite = name;

    public static void True(bool condition, string message)
    {
        if (condition) Passed++;
        else Failures.Add($"[{_suite}] {message}");
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message) where T : IEquatable<T>
        => True(expected.Equals(actual), $"{message}（期望 {expected}，实际 {actual}）");

    public static void Near(double expected, double actual, double tolerance, string message)
        => True(Math.Abs(expected - actual) <= tolerance,
            $"{message}（期望 {expected:F4} ±{tolerance:F4}，实际 {actual:F4}）");

    public static void NotNaN(double value, string message)
        => True(!double.IsNaN(value), $"{message}（实际为 NaN）");

    public static void IsNaN(double value, string message)
        => True(double.IsNaN(value), $"{message}（实际 {value:F4}，应为 NaN）");

    public static void Contains(string text, string fragment, string message)
        => True(text.Contains(fragment, StringComparison.OrdinalIgnoreCase),
            $"{message}（'{text}' 不含 '{fragment}'）");

    public static void Throws<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
            Failures.Add($"[{_suite}] {message}（未抛出 {typeof(T).Name}）");
        }
        catch (T)
        {
            Passed++;
        }
        catch (Exception ex)
        {
            Failures.Add($"[{_suite}] {message}（抛出 {ex.GetType().Name} 而非 {typeof(T).Name}）");
        }
    }
}
