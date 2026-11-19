using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;
namespace QCS
{
    internal sealed class MsdsLearningEntry
    {
        internal string Id = "", Key = "", Hash = "", At = "", User = "", Method = "";
        internal string? Before, After, Label, Text, Context, Candidate;
        internal int? Page;
        internal string Type = "SAME";
        internal string? Subtype;
    }
    internal static class MsdsLearningPersistence
    {
        // Called only inside the registration write transaction, after the parent INSERT.
        private static string Next(SqliteConnection db, SqliteTransaction tx, string table, string column, string prefix, DateTimeOffset now)
        {
            string stem=prefix+now.ToString("yy", CultureInfo.InvariantCulture);
            using var cmd=db.CreateCommand(); cmd.Transaction=tx;
            cmd.CommandText=$"SELECT COALESCE(MAX(CAST(substr({column},5) AS INTEGER)),0) FROM {table} WHERE {column} LIKE $stem";
            cmd.Parameters.AddWithValue("$stem",stem+"%");
            long n=Convert.ToInt64(cmd.ExecuteScalar())+1;
            if(n>999999) throw new InvalidOperationException("AI 학습 연간 채번 한도 초과");
            return stem+n.ToString("D6",CultureInfo.InvariantCulture);
        }
        private static void Insert(SqliteConnection db, SqliteTransaction tx, string table, string prefix, Dictionary<string,object?> map)
        {
            using var cmd=db.CreateCommand(); cmd.Transaction=tx;
            var cols=new List<string>(); var args=new List<string>();
            foreach(var pair in map) { cols.Add(prefix+pair.Key); args.Add("$"+pair.Key); cmd.Parameters.AddWithValue("$"+pair.Key,pair.Value??DBNull.Value); }
            cmd.CommandText=$"INSERT INTO {table} ({string.Join(",",cols)}) VALUES ({string.Join(",",args)})"; cmd.ExecuteNonQuery();
        }
        internal static void Save(SqliteConnection db, SqliteTransaction tx, MsdsDbRegistration.Document doc, long msdsId, string path, string hash)
        {
            string? Value(string key)=>doc.Values.TryGetValue(key,out var v)?v:null;
            string? Final(string key)=>Value(key)?.Trim();
            var now=DateTimeOffset.Now; string at=now.ToString("O");
            var created=new Dictionary<string,string>();
            foreach(var e in doc.Learning)
            {
                if(e.Hash!=hash) throw new InvalidOperationException("교정학습 PDF가 분석 당시와 다릅니다.");
                // Do not learn a value that was subsequently edited without approval.
                if(!string.Equals(Final(e.Key),e.After?.Trim(),StringComparison.Ordinal)) continue;
                var id=Next(db,tx,"ai_correctionh_hist","ai_correctionh_id","AC",now);
                Insert(db,tx,"ai_correctionh_hist","ai_correctionh_",new() {
                    ["id"]=id,["msds_id"]=msdsId,["document_type"]=doc.MaterialType,["pdf_path"]=path,["pdf_hash"]=hash,
                    ["field_key"]=e.Key,["original_value"]=e.Before,["corrected_value"]=Final(e.Key),["corrected_at"]=at,
                    ["source"]="USER_CONFIRMED",["source_label"]=e.Label,["source_text"]=e.Text,["source_context"]=e.Context,
                    ["source_page"]=e.Page,["candidate_value"]=e.Candidate,["correction_method"]=e.Method,["corrected_by"]=e.User,
                    ["learning_status"]="APPROVED",["supplier_name"]=Value("SupplierNameBox"),["product_name"]=Value("ProductNameBox") });
                created[e.Key]=id;
            }
            foreach(var e in doc.Applied)
            {
                if(e.Hash!=hash) throw new InvalidOperationException("학습 적용 PDF가 분석 당시와 다릅니다.");
                bool changed=!string.Equals(Final(e.Key),e.After?.Trim(),StringComparison.Ordinal);
                Insert(db,tx,"ai_applyh_hist","ai_applyh_",new() {
                    ["id"]=Next(db,tx,"ai_applyh_hist","ai_applyh_id","AA",now),["correction_id"]=e.Id,["msds_id"]=msdsId,
                    ["pdf_path"]=path,["pdf_hash"]=hash,["document_type"]=doc.MaterialType,["supplier_name"]=Value("SupplierNameBox"),["product_name"]=Value("ProductNameBox"),
                    ["field_key"]=e.Key,["type"]=e.Type,["subtype"]=e.Type=="SAME"?"S00":e.Subtype,["value_before"]=e.Before,["value_after"]=e.After,["value_final"]=Final(e.Key),
                    ["source_page"]=e.Page,["source_label"]=e.Label,["source_text"]=e.Text,["source_context"]=e.Context,["applied_at"]=e.At,
                    ["status"]=changed?"RECORRECTED":"CONFIRMED",["finalized_by"]=Environment.UserName,["finalized_at"]=at,
                    ["new_correction_id"]=changed&&created.TryGetValue(e.Key,out var id)?id:null });
            }
        }
    }
}
