using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using QCS.Analysis;

namespace QCS
{
    // First S03 rule: exact, independently verified Section 11 heading only.
    internal static class MsdsSimilarDocumentLearning
    {
        private const string Field = "msds_corrosive_flag";
        private const string Key = "CorrosivityBox";
        private static string Norm(string? s) => Regex.Replace((s ?? "").Normalize(System.Text.NormalizationForm.FormKC), @"\s+", "").ToUpperInvariant();
        private static bool Ipk(string? s) => Norm(s) is "(주)아이피케이" or "아이피케이" or "주식회사아이피케이";
        private static bool Format(MsdsPdfTextResult pdf, Action<string> log, string name)
        {
            var first = Norm(pdf.Pages.FirstOrDefault()?.Text);
            var missing = new[]{"물질안전보건자료","제품식별명","SDSCODE","제품등록번호","공급자정보","아이피케이","최초작성일자/최종개정일자"}.Where(x => !first.Contains(x, StringComparison.Ordinal)).ToArray();
            log($"{name} 양식 검사: {(missing.Length == 0 ? "일치" : "누락=" + string.Join(", ", missing))}");
            return missing.Length == 0;
        }
        private static (int Page, string Text, string Context)? Locate(MsdsPdfTextResult pdf, Action<string> log, string name)
        {
            var hits = new List<(int,string,string)>();
            bool section11 = false;
            int headings = 0;
            foreach(var page in pdf.Pages)
            {
                var lines = Regex.Split(page.Text, @"\r?\n");
                for(int i=0;i<lines.Length;i++)
                {
                    var line=Norm(lines[i]);
                    if(Regex.IsMatch(line,@"^11\.독성에관한정보")) { section11=true; headings++; }
                    else if(Regex.IsMatch(line,@"^(?:1[2-6]|[1-9]|10)\.\D")) section11=false;
                    if(section11 && line=="자극성/부식성")
                        hits.Add((page.PageNumber,lines[i].Trim(),string.Join("\n",lines.Skip(Math.Max(0,i-2)).Take(5))));
                }
            }
            log($"{name} SECTION 11 제목 {headings}건, 자극성/부식성 독립행 {hits.Count}건");
            return hits.Count==1?hits[0]:null;
        }
        internal static async Task<List<MsdsLearningEntry>> ApplyAsync(string path,string materialType,MsdsAnalysisResult result,List<MsdsLearningEntry> same,CancellationToken ct, Action<string> log)
        {
            var output=new List<MsdsLearningEntry>();
            string? Get(string key)=>result.Fields.FirstOrDefault(f=>f.FieldKey==key)?.Value;
            var supplier=Get("msds_supplier_name"); var product=Get("msds_product_name");
            log($"대상: 구분={materialType}, 공급업체={supplier}, 제품={product}");
            if(materialType!="FG") { log("제외: FG 아님"); return output; }
            if(!Ipk(supplier)) { log("제외: IPK 공급업체 불일치"); return output; }
            if(string.IsNullOrWhiteSpace(product)) { log("제외: 제품명 없음"); return output; }
            if(same.Any(e=>e.Key==Key)) { log("제외: SAME 우선 적용"); return output; }
            if(!string.IsNullOrWhiteSpace(Get(Field))) { log("제외: 기존 추출값 존재"); return output; }
            using(var common=new SqliteConnection(DatabaseManager.GetConnectionString("common")))
            {
                common.Open(); using var c=common.CreateCommand();
                c.CommandText="SELECT COUNT(*) FROM zx_code_mstr WHERE zx_code_fldname='AI_SIMILAR_METHOD' AND zx_code_value='S03'";
                if(Convert.ToInt64(c.ExecuteScalar())!=1) { log("제외: S03 공통코드 등록 건수 불일치"); return output; }
            }
            string hash; using(var stream=System.IO.File.OpenRead(path)) hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            var candidates=new List<(string Id,string Path,string Hash)>();
            using(var db=new SqliteConnection(DatabaseManager.GetConnectionString("msds")))
            {
                db.Open(); using var c=db.CreateCommand();
                c.CommandText=@"SELECT ai_correctionh_id,ai_correctionh_pdf_path,ai_correctionh_pdf_hash,ai_correctionh_supplier_name,ai_correctionh_product_name,ai_correctionh_corrected_value,ai_correctionh_source_text,ai_correctionh_source_context,ai_correctionh_learning_status FROM ai_correctionh_hist WHERE ai_correctionh_field_key=$key AND ai_correctionh_document_type='FG' ORDER BY ai_correctionh_corrected_at DESC,ai_correctionh_id DESC";
                c.Parameters.AddWithValue("$key",Key);
                using var r=c.ExecuteReader(); var seen=new HashSet<string>();
                while(r.Read())
                {
                    if(Enumerable.Range(0,9).Any(r.IsDBNull)) { log("학습 후보 제외: 필수 데이터 NULL"); continue; }
                    var id = r.GetString(0);
                    if(!Ipk(r.GetString(3))) { log($"{id} 제외: 공급업체 불일치"); continue; }
                    if(Norm(r.GetString(4))==Norm(product)) { log($"{id} 제외: 동일 제품"); continue; }
                    if(r.GetString(2)==hash) { log($"{id} 제외: 동일 Hash"); continue; }
                    if(r.GetString(8)!="APPROVED") { log($"{id} 제외: 승인 상태 아님"); continue; }
                    if(Norm(r.GetString(5))!="자극성/부식성" || Norm(r.GetString(6))!="자극성/부식성" || !Norm(r.GetString(7)).Contains("독성에관한정보")) { log($"{id} 제외: 교정값/원문/문맥 조건 불일치"); continue; }
                    if(!seen.Add(r.GetString(2))) { log($"{id} 제외: 이미 선정된 Hash"); continue; }
                    candidates.Add((r.GetString(0),r.GetString(1),r.GetString(2)));
                }
            }
            log($"유효 학습 후보 {candidates.Count}건");
            if(candidates.Count==0) return output;
            var extractor=new MsdsPdfTextExtractor(); var target=await extractor.ExtractAsync(path,ct);
            if(!Format(target,log,"대상") || Locate(target,log,"대상") is not { } hit) return output;
            foreach(var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if(!System.IO.File.Exists(candidate.Path)) { log($"{candidate.Id} 제외: 원본 PDF 접근 불가"); continue; }
                try
                {
                    using(var stream=System.IO.File.OpenRead(candidate.Path)) if(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))!=candidate.Hash) { log($"{candidate.Id} 제외: 원본 Hash 불일치"); continue; }
                    var original=await extractor.ExtractAsync(candidate.Path,ct);
                    if(!Format(original,log,candidate.Id) || Locate(original,log,candidate.Id)==null) continue;
                }
                catch(System.IO.IOException ex) { log($"{candidate.Id} 원본 읽기 실패: {ex.Message}"); continue; }
                log($"{candidate.Id} 적용: 대상 페이지 {hit.Page} (검증 필요)");
                output.Add(new MsdsLearningEntry { Id=candidate.Id, Key=Key, Hash=hash, Before=Get(Field), After=hit.Text, Text=hit.Text, Page=hit.Page, Context=hit.Context, Label="SECTION 11", At=DateTimeOffset.Now.ToString("O"), Type="SIMILAR", Subtype="S03" });
                result.Fields.RemoveAll(f=>f.FieldKey==Field);
                result.Fields.Add(new MsdsExtractedField { FieldKey=Field, Value=hit.Text, SourceText=hit.Text, SourcePage=hit.Page, Status=MsdsFieldStatus.NeedsReview });
                break;
            }
            return output;
        }
    }
}
