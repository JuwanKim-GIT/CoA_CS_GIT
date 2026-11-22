using System;
using System.IO;
using Microsoft.Data.Sqlite;
using QCS.Analysis;
using QCS.Stage2SqliteChecks;

// Test-only integration: extraction-shaped input -> actual decision engine -> test SQLite transaction.
// No PDF parser, production QCS invocation, or operational database connection.
string path = Path.Combine(Path.GetTempPath(), "qcs_stage2_pipeline_" + Guid.NewGuid().ToString("N") + ".db");
int passed = 0;
try
{
    var store = new SqliteCorrectionStore(path);
    string hash = new('a', 64);
    int Count(string table)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
    void Check(string name, bool ok)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
        if (!ok) throw new Exception(name);
        passed++;
    }
    var input = new AiCorrectionInput("msds_manufacturer", "Old", hash, "ACME", "Section 1");
    var rule = new AiCorrectionRule("R1", 1, AiRuleKind.DocumentSpecific, "msds_manufacturer", hash, "ACME", "New", true, true);
    var decision = AiCorrectionDecisionEngine.Evaluate(input, new[] { rule });
    Check("decision-apply", decision.Kind == AiDecisionKind.Apply && decision.ChangesValue && decision.Value == "New");
    var snapshot = new AiCorrectionSnapshot("S1", hash, input.FieldKey, input.CurrentValue, input.SourceText, input.SourceContext);
    var audit = new AiCorrectionAudit("E1", "S1", "R1", 1, decision.Kind, decision.Reason, input.CurrentValue, decision.Value, decision.ChangesValue);
    AiCorrectionPersistenceContract.SaveAtomically(store, snapshot, rule, audit);
    Check("decision-to-sqlite", Count("test_snapshot") == 1 && Count("test_rule") == 1 && Count("test_audit") == 1);
    AiCorrectionPersistenceContract.SaveAtomically(store, snapshot, rule, audit);
    Check("idempotent-evaluation", Count("test_audit") == 1);
    Check("snapshot-unchanged-in-memory", input.CurrentValue == "Old");
    var corrosiveInput = input with { FieldKey = "msds_corrosive_flag" };
    var corrosiveRule = rule with { Id = "R2", FieldKey = "msds_corrosive_flag" };
    var corrosive = AiCorrectionDecisionEngine.Evaluate(corrosiveInput, new[] { corrosiveRule });
    Check("corrosivity-no-apply", corrosive.Kind == AiDecisionKind.Suggest && !corrosive.ChangesValue && corrosive.Reason == "CORROSIVITY_CONTRACT_PENDING");
    var twaInput = input with { FieldKey = "xylene_twa" };
    var twaRule = rule with { Id = "R3", FieldKey = "xylene_twa", ReplacementValue = "100 ppm" };
    var twa = AiCorrectionDecisionEngine.Evaluate(twaInput, new[] { twaRule });
    Check("twa-no-apply", twa.Kind == AiDecisionKind.Suggest && !twa.ChangesValue);
    Check("protected-decisions-no-new-write", Count("test_audit") == 1);
    try
    {
        AiCorrectionPersistenceContract.SaveAtomically(store, snapshot with { SnapshotId = "S2" }, rule with { ReplacementValue = "Conflicting" }, audit with { EvaluationId = "E2", SnapshotId = "S2" });
        throw new Exception("Expected conflict was not raised");
    }
    catch (InvalidOperationException) { }
    Check("rollback-on-rule-conflict", Count("test_snapshot") == 1 && Count("test_audit") == 1);
    Console.WriteLine($"QCS Stage2 decision-to-SQLite pipeline: {passed}/8 checks passed; test-only temporary DB.");
    Console.WriteLine("NOT TESTED: actual PDF parsing, production QCS runtime, full S00-S06 runtime, operational DB integration.");
}
finally
{
    SqliteConnection.ClearAllPools();
    foreach (string suffix in new[] { "", "-wal", "-shm" })
        try { File.Delete(path + suffix); } catch (IOException) { }
}
