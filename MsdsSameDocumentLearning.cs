using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using QCS.Analysis;

namespace QCS
{
    internal static class MsdsSameDocumentLearning
    {
        private static readonly Dictionary<string,string> Keys = new()
        {
            ["ProductNameBox"]="msds_product_name", ["SupplierNameBox"]="msds_supplier_name",
            ["RevisionDateBox"]="msds_revision_date", ["RevisionNumberBox"]="msds_revision_number",
            ["PhoneBox"]="msds_supplier_phone", ["AddressBox"]="msds_supplier_address",
            ["UnNumberBox"]="msds_un_number", ["HazardCodesBox"]="msds_hazard_codes",
            ["AutoIgnitionBox"]="msds_autoignition", ["VaporPressureBox"]="msds_vapor_pressure",
            ["LelBox"]="msds_lel", ["UelBox"]="msds_uel", ["FlashPointBox"]="msds_flash_point",
            ["CorrosivityBox"]="msds_corrosive_flag", ["PersonalProtectionBox"]="msds_ppe"
        };
        internal static List<MsdsLearningEntry> Apply(string path, MsdsAnalysisResult result)
        {
            using var stream=System.IO.File.OpenRead(path);
            var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            using var conn=new SqliteConnection(DatabaseManager.GetConnectionString("msds")); conn.Open();
            using var cmd=conn.CreateCommand();
            cmd.CommandText="PRAGMA table_info(ai_correctionh_hist)";
            var columns=new HashSet<string>();
            using(var r=cmd.ExecuteReader()) while(r.Read()) columns.Add(r.GetString(1));
            if(!new[]{"pdf_hash","learning_status","source_text","source_page"}.All(k=>columns.Contains("ai_correctionh_"+k))) return new();
            cmd.CommandText=@"SELECT ai_correctionh_field_key, ai_correctionh_corrected_value, ai_correctionh_source_text, ai_correctionh_source_page, ai_correctionh_learning_status, ai_correctionh_id, ai_correctionh_source_label, ai_correctionh_source_context
              FROM ai_correctionh_hist WHERE ai_correctionh_pdf_hash=@hash ORDER BY ai_correctionh_id DESC";
            cmd.Parameters.AddWithValue("@hash",hash);
            var applied=new List<MsdsLearningEntry>(); var seen=new HashSet<string>(); var updates=new List<MsdsExtractedField>();
            using(var r=cmd.ExecuteReader()) while(r.Read())
            {
                var key=r.GetString(0);
                if(!seen.Add(key) || !Keys.TryGetValue(key,out var fieldKey) || r.IsDBNull(4) || r.GetString(4)!="APPROVED" || r.IsDBNull(1) || r.IsDBNull(2) || r.IsDBNull(3)) continue;
                var value=r.GetString(1); var source=r.GetString(2); var page=r.GetInt32(3);
                if(string.IsNullOrWhiteSpace(source) || page<=0) continue;
                if((key=="UelBox" || key=="LelBox") && !System.Text.RegularExpressions.Regex.IsMatch(value, @"^\s*(?:자료\s*없음|해당\s*없음|N/?A|[<>≤≥~\-\d.,%\s]+(?:vol\.?\s*%|ppm)?)\s*$",System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                applied.Add(new MsdsLearningEntry { Id=r.GetString(5), Key=key, Before=result.Fields.FirstOrDefault(f=>f.FieldKey==fieldKey)?.Value, After=value, Hash=hash, Text=source, Page=page, Label=r.IsDBNull(6)?null:r.GetString(6), Context=r.IsDBNull(7)?null:r.GetString(7), At=DateTimeOffset.Now.ToString("O") });
                updates.Add(new MsdsExtractedField {FieldKey=fieldKey, Value=value, SourceText=source, SourcePage=page, Status=MsdsFieldStatus.ManuallyEntered});
            }
            foreach(var field in updates) { result.Fields.RemoveAll(f=>f.FieldKey==field.FieldKey); result.Fields.Add(field); }
            return applied;
        }
    }
}
