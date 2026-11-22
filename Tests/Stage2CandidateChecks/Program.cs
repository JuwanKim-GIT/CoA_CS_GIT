using QCS.Analysis;

const string hashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const string hashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
var source = new AiCorrectionInput("msds_manufacturer", "Original", hashA, "ACME", "Section 1", "1330-20-7");
var documentRule = new AiCorrectionRule("D1", 1, AiRuleKind.DocumentSpecific, "msds_manufacturer", hashA, null, "Corrected", true, true);
var expressionRule = new AiCorrectionRule("E1", 1, AiRuleKind.Expression, "msds_manufacturer", null, "ACME", "ACME", true, true);
var conditionalRule = new AiCorrectionRule("C1", 1, AiRuleKind.Conditional, "msds_manufacturer", null, "ACME", "ACME", true, true, "Section 1", "1330-20-7");
var checks = new (string Name, AiCorrectionInput Input, AiCorrectionRule[] Rules, AiDecisionKind Kind, string Reason, bool Changed)[] {
    ("document-match", source, new[]{documentRule}, AiDecisionKind.Apply, "ELIGIBLE", true),
    ("document-hash-mismatch", source with {PdfSha256=hashB}, new[]{documentRule}, AiDecisionKind.Exclude, "NO_ELIGIBLE_RULE", false),
    ("document-invalid-hash", source with {PdfSha256="bad"}, new[]{documentRule}, AiDecisionKind.Exclude, "NO_ELIGIBLE_RULE", false),
    ("document-disabled", source, new[]{documentRule with {Enabled=false}}, AiDecisionKind.Exclude, "NO_ELIGIBLE_RULE", false),
    ("document-unapproved", source, new[]{documentRule with {Approved=false}}, AiDecisionKind.Exclude, "NO_ELIGIBLE_RULE", false),
    ("expression-same-source", source, new[]{expressionRule}, AiDecisionKind.Apply, "ELIGIBLE", true),
    ("expression-source-mismatch", source with {SourceText="OTHER"}, new[]{expressionRule}, AiDecisionKind.Exclude, "NO_ELIGIBLE_RULE", false),
    ("expression-semantic-change", source, new[]{expressionRule with {ReplacementValue="Other"}}, AiDecisionKind.Suggest, "SEMANTIC_REVIEW_REQUIRED", false),
    ("conditional-context-match", source, new[]{conditionalRule}, AiDecisionKind.Apply, "ELIGIBLE", true),
    ("conditional-context-mismatch", source with {SourceContext="Section 9"}, new[]{conditionalRule}, AiDecisionKind.Exclude, "NO_ELIGIBLE_RULE", false),
    ("conditional-cas-mismatch", source with {CasNumber="111-11-1"}, new[]{conditionalRule}, AiDecisionKind.Exclude, "NO_ELIGIBLE_RULE", false),
    ("conditional-cas-missing", source with {CasNumber=null}, new[]{conditionalRule}, AiDecisionKind.Exclude, "NO_ELIGIBLE_RULE", false),
    ("conflicting-document-rules", source, new[]{documentRule,documentRule with {Id="D2",ReplacementValue="Other"}}, AiDecisionKind.Suggest, "CONFLICTING_RULES", false),
    ("unchanged-value", source with {CurrentValue="Corrected"}, new[]{documentRule}, AiDecisionKind.Exclude, "UNCHANGED_VALUE", false),
    ("corrosivity-protected", source with {FieldKey="msds_corrosive_flag"}, new[]{documentRule with {FieldKey="msds_corrosive_flag"}}, AiDecisionKind.Suggest, "CORROSIVITY_CONTRACT_PENDING", false),
    ("twa-review", source with {FieldKey="xylene_twa"}, new[]{documentRule with {FieldKey="xylene_twa",ReplacementValue="100 ppm"}}, AiDecisionKind.Suggest, "SEMANTIC_REVIEW_REQUIRED", false),
    ("stel-review", source with {FieldKey="xylene_stel"}, new[]{documentRule with {FieldKey="xylene_stel",ReplacementValue="150 ppm"}}, AiDecisionKind.Suggest, "SEMANTIC_REVIEW_REQUIRED", false),
    ("missing-field", source with {FieldKey=" "}, new[]{documentRule}, AiDecisionKind.Exclude, "FIELD_MISSING", false),
};
int failures=0;
foreach (var c in checks) {
    var before=c.Input.CurrentValue;
    var result=AiCorrectionDecisionEngine.Evaluate(c.Input,c.Rules);
    bool ok=result.Kind==c.Kind && result.Reason==c.Reason && result.ChangesValue==c.Changed && c.Input.CurrentValue==before;
    Console.WriteLine($"{(ok?"PASS":"FAIL")} {c.Name}: {result.Kind} / {result.Reason} / changed={result.ChangesValue}");
    if (!ok) failures++;
}
Console.WriteLine($"Stage2 candidate matching: {checks.Length-failures}/{checks.Length} checks passed; no production DB writes");
Console.WriteLine("NOT TESTED: production QCS runtime, actual PDF parsing, original S00-S06 runtime, end-to-end learning integration");
Environment.ExitCode=failures==0?0:1;
