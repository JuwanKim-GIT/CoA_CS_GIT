using QCS.Analysis;

var baseline = new AiTwaEvidence("1330-20-7", "1330-20-7", "TWA", "TWA", "100", "100", "ppm", "ppm", "8시간", "8h", "KOSHA", "KOSHA", true);
var cases = new (string Id, AiTwaEvidence Input, AiTwaDecision Expected, string Reason)[]
{
    ("TWA-01", baseline, AiTwaDecision.Apply, "EXPRESSION_ONLY_SAME_EVIDENCE"),
    ("TWA-02", baseline with { ProposedValue="50" }, AiTwaDecision.Exclude, "NUMERIC_VALUE_CHANGED"),
    ("TWA-03", baseline with { TargetCas="100-41-4" }, AiTwaDecision.Exclude, "CAS_MISMATCH"),
    ("TWA-04", baseline with { ProposedUnit="mg/m3" }, AiTwaDecision.Suggest, "UNIT_CONVERSION_REVIEW"),
    ("TWA-05", baseline with { TargetType="STEL" }, AiTwaDecision.Exclude, "EXPOSURE_TYPE_MISMATCH"),
    ("TWA-06", baseline with { TargetAuthority="ACGIH" }, AiTwaDecision.Suggest, "AUTHORITY_MISMATCH"),
    ("TWA-07", baseline with { TargetHours="15 min" }, AiTwaDecision.Suggest, "EXPOSURE_PERIOD_MISMATCH"),
    ("TWA-08", baseline with { SourceProvenanceConfirmed=false }, AiTwaDecision.Suggest, "SOURCE_PROVENANCE_MISSING"),
    ("TWA-09", baseline with { SameDocument=false }, AiTwaDecision.Exclude, "CROSS_DOCUMENT_EVIDENCE"),
    ("TWA-10", baseline with { SourceCas=null }, AiTwaDecision.Suggest, "REQUIRED_CONTEXT_MISSING"),
    ("TWA-11", baseline with { SourceValue="unknown" }, AiTwaDecision.Suggest, "NUMERIC_EVIDENCE_MISSING"),
    ("TWA-12", baseline with { SourceValue="100.0", ProposedValue="100" }, AiTwaDecision.Apply, "EXPRESSION_ONLY_SAME_EVIDENCE"),
    ("TWA-13", baseline with { SourceCas="invalid" }, AiTwaDecision.Suggest, "CAS_FORMAT_REVIEW"),
    ("TWA-14", baseline with { SourceType="LD50" }, AiTwaDecision.Exclude, "EXPOSURE_TYPE_MISMATCH")
};
int pass = 0;
foreach (var c in cases)
{
    var result = AiTwaEvidenceGuard.Evaluate(c.Input);
    bool ok = result.Decision == c.Expected && result.Reason == c.Reason &&
              (result.Decision != AiTwaDecision.Apply || result.SafeValue == c.Input.SourceValue?.Trim());
    Console.WriteLine($"{c.Id}: {(ok ? "PASS" : "FAIL")} [{result.Decision}, {result.Reason}]");
    if (ok) pass++;
}
Console.WriteLine($"Total: {pass}/{cases.Length}");
return pass == cases.Length ? 0 : 1;
