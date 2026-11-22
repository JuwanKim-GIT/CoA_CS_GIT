using QCS.Analysis;

// Read-only replay of the seven S03 rows documented in the two original analysis logs.
// Source text, original hash and complete S00-S06 runtime candidates were not logged;
// this tests the approved field-level safety gate, NOT full historical rule replay.
var file = Path.Combine(AppContext.BaseDirectory, "Data", "S03_SevenCases.csv");
if (!File.Exists(file)) { Console.Error.WriteLine("Missing evidence CSV: " + file); return 2; }
var lines = File.ReadAllLines(file).Skip(1).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
var cases = new List<AiComparisonCase>();
foreach (var line in lines)
{
    var cols = line.Split(','); // evidence fields have no embedded commas
    if (cols.Length != 8 || cols[2] != "CorrosivityBox" || cols[3] != "S03_APPLIED" || cols[4] != "S03_EXCLUDED")
    { Console.Error.WriteLine("Unexpected evidence format: " + line); return 2; }
    var input = new AiCorrectionInput("msds_corrosive_flag", "", null, null, null);
    cases.Add(new AiComparisonCase(cols[0], input, Array.Empty<AiCorrectionRule>(), "피부 자극성 - 분류 2", true));
}
var result = AiCorrectionComparison.Compare(cases);
foreach (var row in result.Rows)
    Console.WriteLine($"{row.CaseId}: {row.NewDecision} / {row.Reason} / legacyChanged={row.LegacyChangedValue} / newChanged={row.NewChangedValue}");
var ok = result.Summary.Total == 7 && result.Summary.NewSuggest == 7 && result.Summary.NewApply == 0 &&
    result.Summary.Disagreements == 7 && result.Rows.All(r => r.Reason == "CORROSIVITY_CONTRACT_PENDING");
Console.WriteLine(ok ? "PASS: S03 seven-case safety-policy replay (7/7); NO production DB writes" : "FAIL: S03 safety-policy replay");
Console.WriteLine("NOT TESTED: original S00/S01/S02/S04/S05/S06 runtime replay; source evidence not fully present in logs.");
return ok ? 0 : 1;
