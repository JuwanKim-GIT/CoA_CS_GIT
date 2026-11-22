using System;
using QCS.Analysis;

internal static class Program
{
    private static int Main()
    {
        var cases = new (string Name, string? S2, string? S11, bool HasS2, AiSkinEvidenceDecision Decision, string Reason, string? Class)[]
        {
            ("SK-01", "피부 부식성/피부 자극성 구분 1B", "자극성/부식성", true, AiSkinEvidenceDecision.Suggest, "CORROSIVITY_CONTRACT_PENDING", "SKIN_CORROSION_1B"),
            ("SK-02", "피부 자극성 분류 2", "자극성/부식성", true, AiSkinEvidenceDecision.Suggest, "CORROSIVITY_CONTRACT_PENDING", "SKIN_IRRITATION_2"),
            ("SK-03", "기타 유해성", "자극성/부식성", true, AiSkinEvidenceDecision.Exclude, "NO_EXPLICIT_SECTION2_CLASSIFICATION", null),
            ("SK-04", "기타 유해성", "피부 자극 없음", true, AiSkinEvidenceDecision.Exclude, "NO_EXPLICIT_SECTION2_CLASSIFICATION", null),
            ("SK-05", "피부 자극성 분류 2", "피부 자극 없음", true, AiSkinEvidenceDecision.Suggest, "SECTION2_SECTION11_CONFLICT_REVIEW", "SKIN_IRRITATION_2"),
            ("SK-06", "동일 공급업체 제품", "자극성/부식성", true, AiSkinEvidenceDecision.Exclude, "NO_EXPLICIT_SECTION2_CLASSIFICATION", null),
            ("SK-07", null, "자극성/부식성", false, AiSkinEvidenceDecision.Exclude, "SECTION2_EVIDENCE_MISSING", null)
        };
        int failed = 0;
        foreach (var c in cases)
        {
            var actual = AiSkinHazardEvidence.Evaluate(c.S2, c.S11, c.HasS2);
            bool pass = actual.Decision == c.Decision && actual.Reason == c.Reason && actual.Classification == c.Class;
            Console.WriteLine($"{c.Name}: {(pass ? "PASS" : "FAIL")} [{actual.Decision}, {actual.Reason}, {actual.Classification}]");
            if (!pass) failed++;
        }
        Console.WriteLine($"Total: {cases.Length - failed}/{cases.Length}");
        return failed == 0 ? 0 : 1;
    }
}
