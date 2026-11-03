using ThermalPilot.Core.Tests;

// 独立测试入口：dotnet run --project tests/ThermalPilot.Core.Tests
try
{
    new DecisionTreeTests().RunAllTests();
    new MsrEncodingTests().RunAllTests();
    new ThermalCalibrationTests().RunAllTests();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"TEST FAILURE: {ex.Message}");
    return 1;
}
