using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
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
            public Dictionary<string, string> Values { get; set; } = new();
            public List<Ingredient> Ingredients { get; set; } = new();
        }
        internal sealed class Result
        {
            public bool Success { get; set; }
            public string Stage { get; set; } = "DB";
            public string Message { get; set; } = "";
        }
        private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
        private static string Value(Document d, string key) => d.Values.TryGetValue(key, out var value) ? value : "";
        private static object Number(double? n) => n.HasValue ? n.Value : DBNull.Value;
        private static (double?, double?, double?) Amounts(string raw)
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
        internal static HashSet<string> GetRegisteredFileNames()
        {
            var cs = DatabaseManager.ActiveConnectionString;
            if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("DB 연결이 설정되지 않았습니다.");
            using var db = new SqliteConnection(cs);
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT msds_file_name FROM msds_mstr WHERE COALESCE(msds_is_deleted,0)=0";
            using var reader = cmd.ExecuteReader();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read()) if (!reader.IsDBNull(0)) names.Add(reader.GetString(0));
            return names;
        }

        internal static Result Save(Document doc)
        {
            string stage = "DB";
            string? copiedPath = null;
            bool copiedByUs = false;
            try
            {
                if (!File.Exists(doc.FilePath)) throw new IOException("원본 PDF 파일이 없습니다.");
                var connStr = DatabaseManager.ActiveConnectionString;
                if (string.IsNullOrWhiteSpace(connStr)) throw new InvalidOperationException("DB 연결이 설정되지 않았습니다.");
                using var db = new SqliteConnection(connStr);
                db.Open();
                using (var pragma = db.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
                    pragma.ExecuteNonQuery();
                }
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(doc.FilePath)));
                using var tx = db.BeginTransaction();
                var fileName = Path.GetFileName(doc.FilePath);
                using (var check = db.CreateCommand())
                {
                    check.Transaction = tx;
                    check.CommandText = "SELECT COUNT(*) FROM msds_mstr WHERE msds_file_name=$name AND COALESCE(msds_is_deleted,0)=0";
                    check.Parameters.AddWithValue("$name", fileName);
                    if (Convert.ToInt64(check.ExecuteScalar()) > 0)
                        throw new InvalidOperationException("동일한 파일명이 이미 DB에 등록되어 있습니다.");
                }
                var destination = Path.Combine(PdfFolder, fileName);
                if (File.Exists(destination))
                    throw new IOException("PDF 보관 폴더에 동일한 파일명이 존재합니다. 수동 확인 후 재등록해 주세요.");
                var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                using (var cmd = db.CreateCommand())
                {
                    cmd.Transaction = tx;
                    var map = new Dictionary<string, object>
                    {
                        ["msds_file_name"] = fileName, ["msds_file_path"] = destination,
                        ["msds_file_hash"] = hash,
                        ["msds_graco_code"] = DbValue(Value(doc,"GracoCodeBox")),
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
                    }
                    using var audit = db.CreateCommand();
                    audit.Transaction = tx;
                    audit.CommandText = @"INSERT INTO audit_log_hist
                        (audit_log_msds_id,audit_log_product_name,audit_log_content)
                        VALUES ($id,$name,'MSDS PDF 등록')";
                    audit.Parameters.AddWithValue("$id", parentId);
                    audit.Parameters.AddWithValue("$name", DbValue(Value(doc,"ProductNameBox")));
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
