using System;
using QCS.Analysis;

internal static class Program
{
    private static int Main()
    {
        var tests = new (string Id, string? S2, string? S11, bool S2Source, bool S11Source, bool SameDoc, bool SupplierOnly, AiSkinEvidenceDecision Decision, string Reason)[]
        {
            ("SAFE-01", "피부 자극성 분류 2", "피부 자극 없음", true, true, true, false, AiSkinEvidenceDecision.Suggest, "SECTION2_SECTION11_CONFLICT_REVIEW"),
            ("SAFE-02", "피부 자극성 없음", "자극성/부식성", true, true, true, false, AiSkinEvidenceDecision.Exclude, "SECTION2_NEGATIVE_CLASSIFICATION"),
            ("SAFE-03", "피부 자극성 분류 2", "자극성/부식성", false, true, true, false, AiSkinEvidenceDecision.Exclude, "SECTION2_EVIDENCE_MISSING"),
            ("SAFE-04", "피부 자극성 분류 2", "자극성/부식성", true, false, true, false, AiSkinEvidenceDecision.Suggest, "SECTION11_PROVENANCE_UNCONFIRMED"),
            ("SAFE-05", "피부 자극성 분류 2", "", true, true, false, false, AiSkinEvidenceDecision.Exclude, "CROSS_DOCUMENT_OR_SUPPLIER_ONLY"),
            ("SAFE-06", "피부 자극성 분류 2", "", true, true, true, true, AiSkinEvidenceDecision.Exclude, "CROSS_DOCUMENT_OR_SUPPLIER_ONLY"),
            ("SAFE-07", "피부 자극성 분류 2; 피부 부식성 구분 1B", "", true, true, true, false, AiSkinEvidenceDecision.Suggest, "SECTION2_MULTIPLE_CLASSIFICATIONS_REVIEW"),
            ("SAFE-08", "피부 자극성 분류 2", "no evidence of skin irritation", true, true, true, false, AiSkinEvidenceDecision.Suggest, "SECTION2_SECTION11_CONFLICT_REVIEW"),
            ("SAFE-09", "피부 자극성 분류 2", "", true, true, true, false, AiSkinEvidenceDecision.Suggest, "CORROSIVITY_CONTRACT_PENDING"),
            ("SAFE-10", "기타 유해성", "자극성/부식성", true, true, true, false, AiSkinEvidenceDecision.Exclude, "NO_EXPLICIT_SECTION2_CLASSIFICATION")
        };
        int passed = 0;
        foreach (var t in tests)
        {
            var r = AiSkinSafetyGuard.Evaluate(t.S2, t.S11, t.S2Source, t.S11Source, t.SameDoc, t.SupplierOnly);
            bool ok = r.Decision == t.Decision && r.Reason == t.Reason;
            if (ok) passed++;
            Console.WriteLine($"{t.Id}: {(ok ? "PASS" : "FAIL")} [{r.Decision}, {r.Reason}]");
        }
        Console.WriteLine($"Total: {passed}/{tests.Length}");
        return passed == tests.Length ? 0 : 1;
    }
}
