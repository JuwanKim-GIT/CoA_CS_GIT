using System;
using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QCS.Analysis;

namespace QCS.Stage2SqliteChecks;

// Test-only adapter. Never point this at the operational QCS database.
internal sealed class SqliteCorrectionStore : IAiCorrectionTransactionFactory
{
    private readonly string _connectionString;
    internal SqliteCorrectionStore(string filePath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = filePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var db = new SqliteConnection(_connectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS test_snapshot (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS test_rule (id TEXT NOT NULL, version INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(id, version));
            CREATE TABLE IF NOT EXISTS test_audit (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public IAiCorrectionWriteTransaction BeginTransaction() => new Tx(_connectionString);

    private sealed class Tx : IAiCorrectionWriteTransaction
    {
        private readonly SqliteConnection _db;
        private readonly SqliteTransaction _tx;
        private bool _committed;
        internal Tx(string connectionString)
        {
            _db = new SqliteConnection(connectionString);
            _db.Open();
            _tx = _db.BeginTransaction();
        }
        private void InsertUnique(string table, string id, int? version, string payload)
        {
            using var find = _db.CreateCommand();
            find.Transaction = _tx;
            find.CommandText = version.HasValue
                ? $"SELECT payload FROM {table} WHERE id=$id AND version=$version"
                : $"SELECT payload FROM {table} WHERE id=$id";
            find.Parameters.AddWithValue("$id", id);
            if (version.HasValue) find.Parameters.AddWithValue("$version", version.Value);
            var existing = find.ExecuteScalar() as string;
            if (existing is not null)
            {
                if (!string.Equals(existing, payload, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Conflicting duplicate: {table}/{id}");
                return;
            }
            using var insert = _db.CreateCommand();
            insert.Transaction = _tx;
            insert.CommandText = version.HasValue
                ? $"INSERT INTO {table}(id,version,payload) VALUES($id,$version,$payload)"
                : $"INSERT INTO {table}(id,payload) VALUES($id,$payload)";
            insert.Parameters.AddWithValue("$id", id);
            if (version.HasValue) insert.Parameters.AddWithValue("$version", version.Value);
            insert.Parameters.AddWithValue("$payload", payload);
            insert.ExecuteNonQuery();
        }
        public void InsertSnapshotIfAbsent(AiCorrectionSnapshot x) => InsertUnique("test_snapshot", x.SnapshotId, null, JsonSerializer.Serialize(x));
        public void InsertRuleIfAbsent(AiCorrectionRule x) => InsertUnique("test_rule", x.Id, x.Version, JsonSerializer.Serialize(x));
        public void InsertAuditIfAbsent(AiCorrectionAudit x) => InsertUnique("test_audit", x.EvaluationId, null, JsonSerializer.Serialize(x));
        public void Commit() { _tx.Commit(); _committed = true; }
        public void Dispose()
        {
            if (!_committed) _tx.Rollback();
            _tx.Dispose();
            _db.Dispose();
        }
    }
}
