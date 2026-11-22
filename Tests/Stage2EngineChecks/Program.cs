using System;
using QCS.Analysis;

try
{
    var failures = AiCorrectionEngineChecks.Run();
    if (failures.Count == 0)
    {
        Console.WriteLine("QCS Stage 2: all 15 built-in checks passed.");
        return 0;
    }
    foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
    Console.Error.WriteLine($"QCS Stage 2: {failures.Count} check(s) failed.");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 2;
}
