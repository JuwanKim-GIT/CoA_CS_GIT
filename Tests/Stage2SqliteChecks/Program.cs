using System;
using System.IO;
using Microsoft.Data.Sqlite;
using QCS.Analysis;
using QCS.Stage2SqliteChecks;

string path = Path.Combine(Path.GetTempPath(), "qcs_stage2_test_" + Guid.NewGuid().ToString("N") + ".db");
try
{
    var factory = new SqliteCorrectionStore(path);
    var snap = new AiCorrectionSnapshot("S1", new string('a', 64), "msds_manufacturer", "old", "evidence", "context");
    var rule = new AiCorrectionRule("R1", 1, AiRuleKind.DocumentSpecific, "msds_manufacturer", new string('a',64), "evidence", "new", true, true);
    var audit = new AiCorrectionAudit("E1", "S1", "R1", 1, AiDecisionKind.Apply, "TEST", "old", "new", true);
    void Save(AiCorrectionSnapshot s, AiCorrectionRule r, AiCorrectionAudit a) => AiCorrectionPersistenceContract.SaveAtomically(factory, s, r, a);
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
        if (!ok) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS " + name);
    }
    Save(snap, rule, audit);
    Check("atomic-commit", Count("test_snapshot") == 1 && Count("test_rule") == 1 && Count("test_audit") == 1);
    Save(snap, rule, audit);
    Check("idempotent-replay", Count("test_audit") == 1);
    try { Save(snap with { OriginalValue = "changed" }, rule, audit); throw new Exception("Expected duplicate conflict"); }
    catch (InvalidOperationException) { }
    Check("snapshot-immutable", Count("test_snapshot") == 1);
    try { Save(snap with { SnapshotId = "S2" }, rule with { Id = "R2" }, audit with { EvaluationId = "E2", SnapshotId = "S2", RuleId = "R2" }); }
    catch { throw; }
    Check("second-commit", Count("test_audit") == 2);
    // Deliberately fail after a new snapshot insert, on a conflicting rule payload.
    try
    {
        Save(snap with { SnapshotId = "S3" }, rule with { ReplacementValue = "conflict" }, audit with { EvaluationId = "E3", SnapshotId = "S3" });
        throw new Exception("Expected rollback");
    }
    catch (InvalidOperationException) { }
    Check("rollback-on-failure", Count("test_snapshot") == 2 && Count("test_audit") == 2);
    try { Save(snap, rule, audit with { EvaluationId = "E4", RuleVersion = 2 }); throw new Exception("Expected validation error"); }
    catch (ArgumentException) { }
    Check("contract-rejection", Count("test_audit") == 2);
    Console.WriteLine("QCS Stage2 SQLite C#: all 6 checks passed.");
}
finally
{
    SqliteConnection.ClearAllPools();
    foreach (string suffix in new[] { "", "-wal", "-shm" })
        try { File.Delete(path + suffix); } catch (IOException) { }
}
