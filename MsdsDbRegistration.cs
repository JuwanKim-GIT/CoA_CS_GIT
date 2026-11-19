using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace QCS
{
    internal static class MsdsDbRegistration
    {
        private const string PdfFolder = @"\\zcnpo0317vn0001\IPK_PROG\QCS\PDFFiles";
        internal sealed class Ingredient
        {
            public int SortOrder { get; set; }
            public string Name { get; set; } = "";
            public string CasNumber { get; set; } = "";
            public string ConcentrationRaw { get; set; } = "";
        }
        internal sealed class Document
        {
            public string FilePath { get; set; } = "";
            public string MaterialType { get; set; } = "";
            public Dictionary<string, string> Values { get; set; } = new();
            public List<Ingredient> Ingredients { get; set; } = new();
            public List<MsdsLearningEntry> Learning { get; set; } = new();
            public List<MsdsLearningEntry> Applied { get; set; } = new();
        }
        internal sealed class Result
        {
            public bool Success { get; set; }
            public bool Duplicate { get; set; }
            public string Stage { get; set; } = "DB";
            public string Message { get; set; } = "";
        }
        private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
        private static string Value(Document d, string key) => d.Values.TryGetValue(key, out var value) ? value : "";
        private static object Number(double? n) => n.HasValue ? n.Value : DBNull.Value;
        internal static (double?, double?, double?) Amounts(string raw)
        {
            // Only unambiguous numerical expressions are converted; preserve raw string regardless.
            var text = (raw ?? "").Trim();
            var match = Regex.Match(text, @"^\s*(\d+(?:[.,]\d+)?)\s*(?:[-~–]\s*(\d+(?:[.,]\d+)?))?\s*%?\s*$");
            if (!match.Success) return (null, null, null);
            if (!double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var a)) return (null, null, null);
            if (!match.Groups[2].Success) return (a, a, a);
            if (!double.TryParse(match.Groups[2].Value.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var b) || a > b) return (null, null, null);
            return (a, b, (a + b) / 2);
        }
        // Migration is additive; existing MSDS records and their relationships remain unchanged.
        private static void EnsureRegistrationColumns(SqliteConnection db)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(msds_mstr)";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) names.Add(reader.GetString(1));
            }
            foreach (var column in new[] { "msds_material_type", "msds_is_latest", "msds_moel_submission_no" })
            {
                if (names.Contains(column)) continue;
                using var alter = db.CreateCommand();
                alter.CommandText = $"ALTER TABLE msds_mstr ADD COLUMN {column} TEXT";
                alter.ExecuteNonQuery();
            }
        }

        // The common code row stores the pending ledger revision in zx_code_num1.
        // zx_code_cmmt stores the last confirmed ledger revision for reference.
        private const string NextRevisionField = "MSDS_REV_NEXT_NUMBER";

        // Audit schema is managed separately in the DB-first migration procedure.
        private static void RequireAuditSchema(SqliteConnection db)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(audit_log_hist)";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(1));
            foreach (var name in new[] { "audit_log_user_id", "audit_log_action", "audit_log_datetime",
                         "audit_log_graco_code", "audit_log_rev_no" })
                if (!names.Contains(name))
                    throw new InvalidOperationException("Audit DB 변경 SQL을 먼저 적용해 주세요. 누락 필드: " + name);
        }

        private static object? AuditValue(object? value) =>
            value == null || value == DBNull.Value ? null : value;

        private static void AddChanges(List<object> changes, string table, long id,
            IEnumerable<KeyValuePair<string, object>> fields)
        {
            foreach (var entry in fields)
                changes.Add(new { table, record_id = id, field = entry.Key,
                    old = (object?)null, @new = AuditValue(entry.Value) });
        }

        // Audit UPDATE reads the same pending ledger revision from the dedicated common DB.
        // This is read-only and does not issue or increment a revision number.
        internal static long GetPendingRevisionForAudit()
        {
            using var common = new SqliteConnection(DatabaseManager.GetConnectionString("common"));
            common.Open();
            using var cmd = common.CreateCommand();
            cmd.CommandText = @"SELECT zx_code_num1 FROM zx_code_mstr
                WHERE zx_code_fldname=$field AND (zx_code_value='' OR zx_code_value IS NULL)";
            cmd.Parameters.AddWithValue("$field", NextRevisionField);
            var numbers = new List<long>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                    throw new InvalidOperationException("대장 개정번호(zx_code_num1)가 비어 있습니다.");
                numbers.Add(Convert.ToInt64(reader.GetValue(0)));
            }
            if (numbers.Count != 1 || numbers[0] <= 0)
                throw new InvalidOperationException("공통코드 MSDS_REV_NEXT_NUMBER의 zx_code_num1 설정을 확인해 주세요.");
            return numbers[0];
        }

        internal static long GetPendingRevision(SqliteConnection db, SqliteTransaction tx)
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"SELECT zx_code_num1 FROM common.zx_code_mstr
                WHERE zx_code_fldname=$field AND zx_code_value=''";
            cmd.Parameters.AddWithValue("$field", NextRevisionField);
            var numbers = new List<long>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.IsDBNull(0))
                        throw new InvalidOperationException("다음 대장 개정번호(zx_code_num1)가 비어 있습니다.");
                    numbers.Add(reader.GetInt64(0));
                }
            }
            if (numbers.Count != 1 || numbers[0] <= 0)
                throw new InvalidOperationException("공통코드 MSDS_REV_NEXT_NUMBER의 zx_code_num1에 다음 대장 개정번호를 정확히 1건 설정해 주세요.");
            var next = numbers[0];
            using var check = db.CreateCommand();
            check.Transaction = tx;
            check.CommandText = "SELECT COUNT(*) FROM rev_mstr WHERE rev_no=$no";
            check.Parameters.AddWithValue("$no", next);
            if (Convert.ToInt64(check.ExecuteScalar()) != 0)
                throw new InvalidOperationException("다음 대장 개정번호가 이미 확정되어 있습니다. 공통코드를 확인해 주세요.");
            return next;
        }

        internal static string GetRegistrationStatus(string materialType, string gracoCode, string revisionNo = "", string revisionDate = "")
        {
            if (materialType != "RM" && materialType != "FG") throw new ArgumentException("원료/제품 구분을 선택해 주세요.");
            if (string.IsNullOrWhiteSpace(gracoCode)) return "확인필요";
            var cs = DatabaseManager.GetConnectionString("msds");
            if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("DB 연결이 설정되지 않았습니다.");
            using var db = new SqliteConnection(cs);
            db.Open();
            EnsureRegistrationColumns(db);
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT msds_revision_no, msds_revision_date FROM msds_mstr
                WHERE msds_material_type=$type AND msds_graco_code=$code AND COALESCE(msds_is_deleted,0)=0";
            cmd.Parameters.AddWithValue("$type", materialType);
            cmd.Parameters.AddWithValue("$code", gracoCode.Trim());
            bool exists = false;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                exists = true;
                string oldNo = reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString() ?? "";
                string oldDate = reader.IsDBNull(1) ? "" : reader.GetValue(1).ToString() ?? "";
                if ((!string.IsNullOrWhiteSpace(revisionNo) || !string.IsNullOrWhiteSpace(revisionDate)) &&
                    string.Equals(oldNo.Trim(), revisionNo.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(oldDate.Trim(), revisionDate.Trim(), StringComparison.OrdinalIgnoreCase))
                    return "중복";
            }
            return exists ? "개정" : "신규";
        }

        internal static Result Save(Document doc)
        {
            string stage = "DB";
            string? copiedPath = null;
            bool copiedByUs = false;
            try
            {
                if (!File.Exists(doc.FilePath)) throw new IOException("원본 PDF 파일이 없습니다.");
                var connStr = DatabaseManager.GetConnectionString("msds");
                if (string.IsNullOrWhiteSpace(connStr)) throw new InvalidOperationException("DB 연결이 설정되지 않았습니다.");
                using var db = DatabaseManager.OpenConnection("msds");
                using (var pragma = db.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
                    pragma.ExecuteNonQuery();
                }
                if (doc.MaterialType != "RM" && doc.MaterialType != "FG")
                    throw new InvalidOperationException("원료/제품 구분이 지정되지 않았습니다.");
                string gracoCode = Path.GetFileNameWithoutExtension(doc.FilePath).Trim();
                if (string.IsNullOrWhiteSpace(gracoCode)) throw new InvalidOperationException("Graco Code를 확인할 수 없습니다.");
                EnsureRegistrationColumns(db);
                // The pending inventory revision must be configured before MSDS registration.
                // Never create an arbitrary revision number or silently skip the audit entry.
                RequireAuditSchema(db);
                var revisionNo = Value(doc, "RevisionNumberBox").Trim();
                var revisionDate = Value(doc, "RevisionDateBox").Trim();
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(doc.FilePath)));
                using var tx = db.BeginTransaction();
                var pendingRevision = GetPendingRevision(db, tx);
                bool hasExisting = false;
                var changes = new List<object>();
                using (var check = db.CreateCommand())
                {
                    check.Transaction = tx;
                    check.CommandText = @"SELECT msds_revision_no, msds_revision_date FROM msds_mstr
                        WHERE msds_material_type=$type AND msds_graco_code=$code AND COALESCE(msds_is_deleted,0)=0";
                    check.Parameters.AddWithValue("$type", doc.MaterialType);
                    check.Parameters.AddWithValue("$code", gracoCode);
                    using var reader = check.ExecuteReader();
                    while (reader.Read())
                    {
                        hasExisting = true;
                        string oldNo = reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString() ?? "";
                        string oldDate = reader.IsDBNull(1) ? "" : reader.GetValue(1).ToString() ?? "";
                        if ((!string.IsNullOrWhiteSpace(revisionNo) || !string.IsNullOrWhiteSpace(revisionDate)) &&
                            string.Equals(oldNo.Trim(), revisionNo, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(oldDate.Trim(), revisionDate, StringComparison.OrdinalIgnoreCase))
                            return new Result { Duplicate = true, Message = "동일한 개정번호와 개정일자가 등록되어 있습니다." };
                    }
                }
                // Keep the complete original PDF base name, including any internal punctuation.
                var baseName = Path.GetFileNameWithoutExtension(doc.FilePath);
                var fileName = baseName + "-" + DateTime.Now.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture) + ".pdf";
                var destination = Path.Combine(PdfFolder, fileName);
                if (File.Exists(destination)) throw new IOException("저장할 PDF 파일명이 이미 존재합니다. 잠시 후 다시 시도해 주세요.");
                if (hasExisting)
                {
                    using (var previous = db.CreateCommand())
                    {
                        previous.Transaction = tx;
                        previous.CommandText = @"SELECT msds_msds_id, msds_is_latest FROM msds_mstr
                            WHERE msds_material_type=$type AND msds_graco_code=$code AND COALESCE(msds_is_deleted,0)=0";
                        previous.Parameters.AddWithValue("$type", doc.MaterialType);
                        previous.Parameters.AddWithValue("$code", gracoCode);
                        using var reader = previous.ExecuteReader();
                        while (reader.Read())
                        {
                            var oldFlag = reader.IsDBNull(1) ? null : reader.GetValue(1);
                            if (oldFlag?.ToString() != "N")
                                changes.Add(new { table = "msds_mstr", record_id = reader.GetInt64(0),
                                    field = "msds_is_latest", old = oldFlag, @new = (object)"N" });
                        }
                    }
                    using var update = db.CreateCommand();
                    update.Transaction = tx;
                    update.CommandText = @"UPDATE msds_mstr SET msds_is_latest='N'
                        WHERE msds_material_type=$type AND msds_graco_code=$code AND COALESCE(msds_is_deleted,0)=0";
                    update.Parameters.AddWithValue("$type", doc.MaterialType);
                    update.Parameters.AddWithValue("$code", gracoCode);
                    update.ExecuteNonQuery();
                }
                var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                using (var cmd = db.CreateCommand())
                {
                    cmd.Transaction = tx;
                    var map = new Dictionary<string, object>
                    {
                        ["msds_file_name"] = fileName, ["msds_file_path"] = destination,
                        ["msds_file_hash"] = hash,
                        ["msds_graco_code"] = gracoCode,
                        ["msds_material_type"] = doc.MaterialType, ["msds_is_latest"] = "Y",
                        ["msds_moel_submission_no"] = DbValue(Value(doc,"MoelSubmissionBox")),
                        ["msds_product_name"] = DbValue(Value(doc,"ProductNameBox")),
                        ["msds_supplier_name"] = DbValue(Value(doc,"SupplierNameBox")),
                        ["msds_supplier_phone"] = DbValue(Value(doc,"PhoneBox")),
                        ["msds_supplier_address"] = DbValue(Value(doc,"AddressBox")),
                        ["msds_revision_date"] = DbValue(Value(doc,"RevisionDateBox")),
                        ["msds_revision_no"] = DbValue(Value(doc,"RevisionNumberBox")),
                        ["msds_h_codes"] = DbValue(Value(doc,"HazardCodesBox")),
                        ["msds_corrosive_flag"] = DbValue(Value(doc,"CorrosivityBox")),
                        ["msds_ppe"] = DbValue(Value(doc,"PersonalProtectionBox")),
                        ["msds_lel"] = DbValue(Value(doc,"LelBox")),
                        ["msds_uel"] = DbValue(Value(doc,"UelBox")),
                        ["msds_flash_point"] = DbValue(Value(doc,"FlashPointBox")),
                        ["msds_autoignition_temp"] = DbValue(Value(doc,"AutoIgnitionBox")),
                        ["msds_vapor_pressure"] = DbValue(Value(doc,"VaporPressureBox")),
                        ["msds_un_number"] = DbValue(Value(doc,"UnNumberBox")),
                        ["msds_registered_at"] = now, ["msds_updated_at"] = now,
                        ["msds_is_deleted"] = 0
                    };
                    var cols = string.Join(",", map.Keys);
                    var pars = string.Join(",", map.Keys.Select(k => "$" + k));
                    cmd.CommandText = $"INSERT INTO msds_mstr ({cols}) VALUES ({pars}); SELECT last_insert_rowid();";
                    foreach (var entry in map) cmd.Parameters.AddWithValue("$" + entry.Key, entry.Value);
                    var parentId = Convert.ToInt64(cmd.ExecuteScalar());
                    AddChanges(changes, "msds_mstr", parentId, map);
                    foreach (var ingredient in doc.Ingredients)
                    {
                        var (min, max, calculated) = Amounts(ingredient.ConcentrationRaw);
                        using var child = db.CreateCommand();
                        child.Transaction = tx;
                        child.CommandText = @"INSERT INTO msds_comp_dtl
                            (msds_comp_parent_msds_id,msds_comp_name,msds_comp_cas_no,
                             msds_comp_content_raw,msds_comp_content_min,msds_comp_content_max,
                             msds_comp_content_calculated,msds_comp_sort_order)
                            VALUES ($id,$name,$cas,$raw,$min,$max,$calc,$ord)";
                        child.Parameters.AddWithValue("$id", parentId);
                        child.Parameters.AddWithValue("$name", DbValue(ingredient.Name));
                        child.Parameters.AddWithValue("$cas", DbValue(ingredient.CasNumber));
                        child.Parameters.AddWithValue("$raw", DbValue(ingredient.ConcentrationRaw));
                        child.Parameters.AddWithValue("$min", Number(min));
                        child.Parameters.AddWithValue("$max", Number(max));
                        child.Parameters.AddWithValue("$calc", Number(calculated));
                        child.Parameters.AddWithValue("$ord", ingredient.SortOrder);
                        child.ExecuteNonQuery();
                        child.CommandText = "SELECT last_insert_rowid()";
                        var childId = Convert.ToInt64(child.ExecuteScalar());
                        var componentFields = new Dictionary<string, object>
                        {
                            ["msds_comp_parent_msds_id"] = parentId,
                            ["msds_comp_name"] = DbValue(ingredient.Name),
                            ["msds_comp_cas_no"] = DbValue(ingredient.CasNumber),
                            ["msds_comp_content_raw"] = DbValue(ingredient.ConcentrationRaw),
                            ["msds_comp_content_min"] = Number(min),
                            ["msds_comp_content_max"] = Number(max),
                            ["msds_comp_content_calculated"] = Number(calculated),
                            ["msds_comp_sort_order"] = ingredient.SortOrder
                        };
                        AddChanges(changes, "msds_comp_dtl", childId, componentFields);
                    }
                    MsdsLearningPersistence.Save(db, tx, doc, parentId, destination, hash);
                    // One DB save operation creates exactly one audit record.
                    // Registration of a new MSDS revision is an INSERT, not an UPDATE.
                    using var audit = db.CreateCommand();
                    audit.Transaction = tx;
                    audit.CommandText = @"INSERT INTO audit_log_hist
                        (audit_log_msds_id, audit_log_product_name, audit_log_content,
                         audit_log_user_id, audit_log_action, audit_log_datetime,
                         audit_log_graco_code, audit_log_rev_no)
                        VALUES ($id,$name,$content,$user,$action,$date,$code,$rev)";
                    audit.Parameters.AddWithValue("$id", parentId);
                    audit.Parameters.AddWithValue("$name", DbValue(Value(doc,"ProductNameBox")));
                    audit.Parameters.AddWithValue("$content", JsonSerializer.Serialize(new {
                        registration_type = hasExisting ? "REV" : "NEW", changes
                    }));
                    audit.Parameters.AddWithValue("$user", Environment.UserName);
                    audit.Parameters.AddWithValue("$action", "INSERT");
                    audit.Parameters.AddWithValue("$date", now);
                    audit.Parameters.AddWithValue("$code", gracoCode);
                    audit.Parameters.AddWithValue("$rev", pendingRevision);
                    audit.ExecuteNonQuery();
                }
                stage = "PDF";
                Directory.CreateDirectory(PdfFolder);
                File.Copy(doc.FilePath, destination, false);
                copiedPath = destination;
                copiedByUs = true;
                stage = "DB";
                try { tx.Commit(); }
                catch
                {
                    if (copiedByUs) try { File.Delete(copiedPath!); } catch { /* cleanup best effort */ }
                    throw;
                }
                return new Result { Success = true };
            }
            catch (Exception ex)
            {
                // A failed copy can leave a partial file; never remove an existing file.
                if (copiedByUs && copiedPath != null && File.Exists(copiedPath))
                    try { File.Delete(copiedPath); } catch { }
                return new Result { Stage = stage, Message = ex.Message };
            }
        }
    }
}
