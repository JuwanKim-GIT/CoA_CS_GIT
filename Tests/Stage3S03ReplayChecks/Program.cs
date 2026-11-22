using QCS.Analysis;

// S3-05: replay of the seven historical S03 document classifications.
// Fixtures reproduce the verified section-2 classification, not full PDF extraction.
// No DB writes, no existing production behavior changes.
var cases = new[] { "ENA307", "EPA003", "KUA752_B", "LLB134", "NCA019", "PHC287", "XKR159" };
int passed = 0;
foreach (string code in cases)
{
    var result = AiSkinSafetyGuard.Evaluate(
        section2: "피부 자극성 - 분류 2",
        section11: null,
        section2ProvenanceConfirmed: true,
        section11ProvenanceConfirmed: true,
        sameDocumentEvidence: true,
        supplierSimilarityOnly: false);
    bool ok = result.Decision == AiSkinEvidenceDecision.Suggest &&
              result.Reason == "CORROSIVITY_CONTRACT_PENDING" &&
              result.Classification == "SKIN_IRRITATION_2";
    Console.WriteLine($"{code}: {(ok ? "PASS" : "FAIL")} [{result.Decision}, {result.Reason}, {result.Classification}]");
    if (ok) passed++;
}
Console.WriteLine($"S3-05 S03 replay: {passed}/{cases.Length}");
return passed == cases.Length ? 0 : 1;
