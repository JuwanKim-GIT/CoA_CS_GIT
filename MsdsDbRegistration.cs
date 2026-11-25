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
        internal const string PdfFolder = @"\\zcnpo0317vn0001\IPK_PROG\QCS\PDFFiles";
        internal sealed class Ingredient
        {
            public int SortOrder { get; set; }
            public string Name { get; set; } = "";
            public string CasNumber { get; set; } = "";
            public string ConcentrationRaw { get; set; } = "";
            public Ingredient? Original { get; set; }
            public string Twa { get; set; } = "";
            public string Stel { get; set; } = "";
            public string OralLd50 { get; set; } = "";
            public string DermalLd50 { get; set; } = "";
            public string InhalationLc50 { get; set; } = "";

        }
        internal sealed class Document
        {
            public string FilePath { get; set; } = "";
            public string MaterialType { get; set; } = "";
            public Dictionary<string, string> Values { get; set; } = new();
            public Dictionary<string, string> OriginalValues { get; set; } = new();
            public List<Ingredient> Ingredients { get; set; } = new();
            public List<MsdsUnifiedMemory.NaturalStep> NaturalHistory { get; set; } = new();
            public string NaturalActor { get; set; } = "";
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
        internal static string GracoCode(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path).Trim();
            return name.Substring(0, Math.Min(6, name.Length));
        }
        internal static (double?, double?, double?) Amounts(string raw)
        {
            var text = (raw ?? "").Trim().Normalize(System.Text.NormalizationForm.FormKC)
                .Replace("≥", ">=").Replace("≤", "<=").Replace("－", "-")
                .Replace("〜", "~").Replace("～", "~").Replace("–", "-").Replace("—", "-");
            static double? Parse(string value) =>
                double.TryParse(value.Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var n) && n >= 0 && n <= 100 ? n : null;
            const string n = @"(\d+(?:[.,]\d+)?)";
            var range = Regex.Match(text,
                @"^\s*(?:>=|>)?\s*" + n + @"\s*(?:-|~|to)\s*(?:<=|<)?\s*" + n + @"\s*%?\s*$",
                RegexOptions.IgnoreCase);
            if (range.Success)
            {
                var a=Parse(range.Groups[1].Value); var b=Parse(range.Groups[2].Value);
                return a.HasValue && b.HasValue && a <= b ? (a,b,(a.Value+b.Value)/2) : (null,null,null);
            }
            var upper=Regex.Match(text,@"^\s*(?:<=|<)\s*"+n+@"\s*%?\s*$");
            if (upper.Success) return (null,Parse(upper.Groups[1].Value),null);
            var lower=Regex.Match(text,@"^\s*(?:>=|>)\s*"+n+@"\s*%?\s*$");
            if (lower.Success) return (Parse(lower.Groups[1].Value),null,null);
            var exact=Regex.Match(text,@"^\s*"+n+@"\s*%?\s*$");
            if (exact.Success) { var v=Parse(exact.Groups[1].Value); return (v,v,v); }
            return (null,null,null);
        }
        // Schema is managed by the approved local DB migration, never altered during registration.
        private static void EnsureRegistrationColumns(SqliteConnection db)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(msds_mstr)";
            using var reader = cmd.ExecuteReader();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read()) names.Add(reader.GetString(1));
            if (!names.Contains("msds_product_name_ai") || !names.Contains("msds_product_name_mod"))
                throw new InvalidOperationException("MSDS AI/MOD DB 스키마 변경 SQL을 먼저 적용해 주세요.");
        }

        private static readonly (string Column, string UiKey, string AiKey)[] BasicFields =
        {
            ("moel_submission_no","MoelSubmissionBox","msds_moel_submission_no"),
            ("product_name","ProductNameBox","msds_product_name"),
            ("supplier_name","SupplierNameBox","msds_supplier_name"),
            ("supplier_phone","PhoneBox","msds_supplier_phone"),
            ("supplier_address","AddressBox","msds_supplier_address"),
            ("revision_date","RevisionDateBox","msds_revision_date"),
            ("revision_no","RevisionNumberBox","msds_revision_number"),
            ("h_codes","HazardCodesBox","msds_hazard_codes"),
            ("corrosive_flag","CorrosivityBox","msds_corrosive_flag"),
            ("toxicity","ToxicityBox","msds_toxicity"),
            ("ppe","PersonalProtectionBox","msds_ppe"),
            ("lel","LelBox","msds_lel"),
            ("uel","UelBox","msds_uel"),
            ("flash_point","FlashPointBox","msds_flash_point"),
            ("autoignition_temp","AutoIgnitionBox","msds_autoignition"),
            ("vapor_pressure","VaporPressureBox","msds_vapor_pressure"),
            ("un_number","UnNumberBox","msds_un_number"),
            ("molecular_formula","MolecularFormulaBox","msds_molecular_formula"),
            ("physical_state","PhysicalStateBox","msds_physical_state"),
            ("hazardous_goods_class","HazardousGoodsBox","msds_hazardous_goods_class"),
            ("reactivity","ReactivityBox","msds_reactivity")
        };
        private static object OriginalDbValue(string? value) => value == null ? DBNull.Value : value;
        private static object ModifiedDbValue(string? original, string? edited) =>
            string.Equals(original ?? "", edited ?? "", StringComparison.Ordinal) ? DBNull.Value : edited ?? "";
        private static string? OriginalValue(Document doc, string uiKey, string aiKey)
        {
            if (doc.OriginalValues.TryGetValue(aiKey, out var value)) return value;
            // Derived fields (e.g. MOEL number) are captured before user edits.
            return doc.OriginalValues.TryGetValue(uiKey, out value) ? value : null;
        }
        private static string EffectiveRevision(string ai, string mod) =>
            $"COALESCE({mod},{ai})";

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

        private static void RequireMatchingMaterialType(SqliteConnection db, SqliteTransaction? tx, string type, string code)
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"SELECT COUNT(*) FROM msds_mstr WHERE msds_graco_code=$code
                AND COALESCE(msds_is_deleted,0)=0 AND msds_material_type IN ('FG','RM') AND msds_material_type<>$type";
            cmd.Parameters.AddWithValue("$code", code);
            cmd.Parameters.AddWithValue("$type", type);
            if (Convert.ToInt64(cmd.ExecuteScalar()) > 0)
                throw new InvalidOperationException("기존 등록 건의 원료/제품 구분과 일치하지 않습니다.");
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
            RequireMatchingMaterialType(db, null, materialType, gracoCode.Trim());
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT COALESCE(msds_revision_no_mod,msds_revision_no_ai), COALESCE(msds_revision_date_mod,msds_revision_date_ai) FROM msds_mstr
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

        private static void SaveNaturalHistory(SqliteConnection db, SqliteTransaction tx, Document doc,
            long parentId, string graco, string path, string hash, string now)
        {
            if (doc.NaturalHistory.Count == 0) return;
            void Insert(string table, Dictionary<string, object?> values)
            {
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"INSERT INTO {table} ({string.Join(",",values.Keys)}) VALUES ({string.Join(",",values.Keys.Select(k=>"$"+k))})";
                foreach(var pair in values) cmd.Parameters.AddWithValue("$"+pair.Key,pair.Value ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            using var next = db.CreateCommand();
            next.Transaction = tx;
            next.CommandText = "SELECT COALESCE(MAX(CAST(substr(ai_apply_id,3) AS INTEGER)),0)+1 FROM ai_apply_mstr";
            long number = Convert.ToInt64(next.ExecuteScalar());
            if(number > 99999999) throw new InvalidOperationException("적용사례 ID 범위 초과");
            string id = "AA" + number.ToString("D8",CultureInfo.InvariantCulture);
            Insert("ai_apply_mstr",new() {
                ["ai_apply_id"]=id,["ai_apply_msds_id"]=parentId,["ai_apply_graco_code"]=graco,
                ["ai_apply_material_type"]=doc.MaterialType,["ai_apply_product_name"]=Value(doc,"ProductNameBox"),
                ["ai_apply_supplier_name"]=Value(doc,"SupplierNameBox"),["ai_apply_pdf_path"]=path,
                ["ai_apply_pdf_hash"]=hash,["ai_apply_applied_at"]=doc.NaturalHistory[0].At.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture),["ai_apply_applied_by"]=doc.NaturalActor
            });
            int order=0;
            foreach(var step in doc.NaturalHistory)
            {
                var r=step.Target;
                Insert("ai_applyd_det",new() {
                    ["ai_applyd_apply_id"]=id,["ai_applyd_learning_id"]=step.RuleId,["ai_applyd_learning_version"]=step.Version,
                    ["ai_applyd_instruction"]=step.Instruction,["ai_applyd_rule_type"]=step.Kind,["ai_applyd_field_code"]=r.FieldCode,
                    ["ai_applyd_target_item_key"]=$"{r.Category}:{r.Order}:{r.FieldCode}",["ai_applyd_cas_no"]=r.CasNumber,
                    ["ai_applyd_ingredient_name"]=r.IngredientName,["ai_applyd_sequence"]=step.Sequence,["ai_applyd_execution_order"]=++order,
                    ["ai_applyd_value_ai"]=r.ValueAi,["ai_applyd_value_before"]=step.Before,["ai_applyd_value_after"]=step.After,
                    ["ai_applyd_value_final"]=doc.NaturalHistory.Last(x=>ReferenceEquals(x.Target,r)).After,["ai_applyd_source_page"]=step.Page,["ai_applyd_source_text"]=step.Evidence,
                    ["ai_applyd_applied_at"]=step.At.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture),["ai_applyd_status"]=step.Status,["ai_applyd_error"]=step.Error
                });
            }
        }

        internal static Result Save(Document doc, string? pdfFolder = null)
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
                string gracoCode = GracoCode(doc.FilePath);
                if (string.IsNullOrWhiteSpace(gracoCode)) throw new InvalidOperationException("Graco Code를 확인할 수 없습니다.");
                EnsureRegistrationColumns(db);
                // The pending inventory revision must be configured before MSDS registration.
                // Never create an arbitrary revision number or silently skip the audit entry.
                RequireAuditSchema(db);
                var revisionNo = Value(doc, "RevisionNumberBox").Trim();
                var revisionDate = Value(doc, "RevisionDateBox").Trim();
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(doc.FilePath)));
                using var tx = db.BeginTransaction();
                RequireMatchingMaterialType(db, tx, doc.MaterialType, gracoCode);
                var pendingRevision = GetPendingRevision(db, tx);
                bool hasExisting = false;
                var changes = new List<object>();
                using (var check = db.CreateCommand())
                {
                    check.Transaction = tx;
                    check.CommandText = @"SELECT COALESCE(msds_revision_no_mod,msds_revision_no_ai), COALESCE(msds_revision_date_mod,msds_revision_date_ai) FROM msds_mstr
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
                // Use the canonical Graco code for newly registered PDFs.
                var baseName = gracoCode;
                var fileName = baseName + "-" + DateTime.Now.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture) + ".pdf";
                var destination = Path.Combine(pdfFolder ?? PdfFolder, fileName);
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
                        // AI extraction and user modifications are stored independently.
                        ["msds_registered_at"] = now, ["msds_updated_at"] = now,
                        ["msds_is_deleted"] = 0
                    };
                    foreach (var field in BasicFields)
                    {
                        var original = OriginalValue(doc, field.UiKey, field.AiKey);
                        var edited = Value(doc, field.UiKey);
                        map["msds_" + field.Column + "_ai"] = OriginalDbValue(original);
                        map["msds_" + field.Column + "_mod"] = ModifiedDbValue(original, edited);
                    }
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
                        var original = ingredient.Original;
                        var (oMin,oMax,oCalc) = Amounts(original?.ConcentrationRaw ?? "");
                        static string NumText(double? number) => number?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
                        var componentFields = new Dictionary<string, object>
                        {
                            ["msds_comp_parent_msds_id"] = parentId,
                            ["msds_comp_name"] = DbValue(ingredient.Name),
                            ["msds_comp_cas_no"] = DbValue(ingredient.CasNumber),
                            ["msds_comp_sort_order"] = ingredient.SortOrder
                        };
                        void AddPair(string name, string? ai, string? edited)
                        {
                            componentFields["msds_comp_" + name + "_ai"] = OriginalDbValue(ai);
                            componentFields["msds_comp_" + name + "_mod"] = ModifiedDbValue(ai, edited);
                        }
                        AddPair("content_raw", original?.ConcentrationRaw, ingredient.ConcentrationRaw);
                        AddPair("content_min", original == null ? null : NumText(oMin), NumText(min));
                        AddPair("content_max", original == null ? null : NumText(oMax), NumText(max));
                        AddPair("content_calculated", original == null ? null : NumText(oCalc), NumText(calculated));
                        AddPair("twa", original?.Twa, ingredient.Twa);
                        AddPair("stel", original?.Stel, ingredient.Stel);
                        AddPair("oral_ld50", original?.OralLd50, ingredient.OralLd50);
                        AddPair("dermal_ld50", original?.DermalLd50, ingredient.DermalLd50);
                        AddPair("inhalation_lc50", original?.InhalationLc50, ingredient.InhalationLc50);
                        var colsChild = string.Join(",", componentFields.Keys);
                        var parsChild = string.Join(",", componentFields.Keys.Select(k => "$" + k));
                        child.CommandText = $"INSERT INTO msds_comp_dtl ({colsChild}) VALUES ({parsChild}); SELECT last_insert_rowid();";
                        foreach (var entry in componentFields) child.Parameters.AddWithValue("$" + entry.Key, entry.Value);
                        var childId = Convert.ToInt64(child.ExecuteScalar());
                        AddChanges(changes, "msds_comp_dtl", childId, componentFields);
                    }
                    SaveNaturalHistory(db, tx, doc, parentId, gracoCode, destination, hash, now);
                    // Legacy AI correction learning persistence remains disabled.
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
                Directory.CreateDirectory(pdfFolder ?? PdfFolder);
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
