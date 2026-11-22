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
                    if(!IsSkinClassification(r.GetString(5)) || !ContainsSkinClassification(r.GetString(6) + "\n" + r.GetString(7), r.GetString(5))) { log($"{id} 제외: 교정값/원문/문맥 조건 불일치"); continue; }
                    if(!seen.Add(r.GetString(2))) { log($"{id} 제외: 이미 선정된 Hash"); continue; }
                    candidates.Add((r.GetString(0),r.GetString(1),r.GetString(2)));
                }
            }
            log($"유효 학습 후보 {candidates.Count}건");
            if(candidates.Count==0) return output;
            var extractor=new MsdsPdfTextExtractor(); var target=await extractor.ExtractAsync(path,ct);
            if(!Format(target,log,"대상")) return output;
            foreach(var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if(!System.IO.File.Exists(candidate.Path)) { log($"{candidate.Id} 제외: 원본 PDF 접근 불가"); continue; }
                try
                {
                    using(var stream=System.IO.File.OpenRead(candidate.Path)) if(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))!=candidate.Hash) { log($"{candidate.Id} 제외: 원본 Hash 불일치"); continue; }
                    var original=await extractor.ExtractAsync(candidate.Path,ct);
                    if(!Format(original,log,candidate.Id)) continue;
                }
                catch(System.IO.IOException ex) { log($"{candidate.Id} 원본 읽기 실패: {ex.Message}"); continue; }
                // 학습 문서의 분류를 다른 제품에 복사하지 않는다.
                // 대상 문서에 동일한 분류가 명시된 경우에만 적용한다.
                var learnedClass = "";
                using (var db = new SqliteConnection(DatabaseManager.GetConnectionString("msds")))
                {
                    db.Open(); using var cmd = db.CreateCommand();
                    cmd.CommandText = "SELECT ai_correctionh_corrected_value FROM ai_correctionh_hist WHERE ai_correctionh_id=$id";
                    cmd.Parameters.AddWithValue("$id", candidate.Id);
                    learnedClass = cmd.ExecuteScalar()?.ToString() ?? "";
                }
                if (!ContainsSkinClassification(target.FullText, learnedClass))
                {
                    log($"{candidate.Id} 검토 제안: 대상 문서에 동일한 피부 분류 근거 없음");
                    continue;
                }
                var hitPage = target.Pages.FirstOrDefault(p => ContainsSkinClassification(p.Text, learnedClass));
                if (hitPage == null) continue;
                log($"{candidate.Id} 적용: 대상 페이지 {hitPage.PageNumber} (검증 필요)");
                output.Add(new MsdsLearningEntry { Id=candidate.Id, Key=Key, Hash=hash, Before=Get(Field), After=learnedClass, Text=learnedClass, Page=hitPage.PageNumber, Context="SECTION 2 분류 원문 확인", Label="SECTION 2", At=DateTimeOffset.Now.ToString("O"), Type="SIMILAR", Subtype="S03" });
                result.Fields.RemoveAll(f=>f.FieldKey==Field);
                result.Fields.Add(new MsdsExtractedField { FieldKey=Field, Value=learnedClass, SourceText=learnedClass, SourcePage=hitPage.PageNumber, Status=MsdsFieldStatus.NeedsReview });
                break;
            }
            return output;
        }
        // 보수적인 성분별 유사학습: 동일 CAS + 동일 물성 + 수치/단위 동등성이
        // 확인되는 표현 정규화만 자동 적용한다. 서로 다른 독성값은 전파하지 않는다.
        internal static async Task<List<MsdsLearningEntry>> ApplyIngredientAsync(
            string path, string materialType, MsdsAnalysisResult result,
            List<MsdsLearningEntry> already, CancellationToken ct, Action<string> log)
        {
            var output = new List<MsdsLearningEntry>();
            var supplier = result.Fields.FirstOrDefault(f => f.FieldKey == "msds_supplier_name")?.Value ?? "";
            var product = result.Fields.FirstOrDefault(f => f.FieldKey == "msds_product_name")?.Value ?? "";
            var hash = "";
            using (var stream = System.IO.File.OpenRead(path))
                hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            var candidates = new List<(string Id, string Key, string Value, string Supplier, string Product, string Pdf, string Hash, string Source, string Original, int Page)>();
            using (var db = new SqliteConnection(DatabaseManager.GetConnectionString("msds")))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"SELECT ai_correctionh_id, ai_correctionh_field_key, ai_correctionh_corrected_value,
                    ai_correctionh_supplier_name, ai_correctionh_product_name, ai_correctionh_pdf_path,
                    ai_correctionh_pdf_hash, ai_correctionh_source_text, ai_correctionh_original_value, ai_correctionh_source_page
                    FROM ai_correctionh_hist
                    WHERE ai_correctionh_learning_status='APPROVED'
                    AND ai_correctionh_document_type=$type
                    AND ai_correctionh_field_key LIKE 'ING:%'
                    ORDER BY ai_correctionh_corrected_at DESC, ai_correctionh_id DESC";
                cmd.Parameters.AddWithValue("$type", materialType);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (Enumerable.Range(0, 8).Any(reader.IsDBNull) || reader.IsDBNull(9)) continue;
                    candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                        reader.GetString(7), reader.IsDBNull(8) ? "" : reader.GetString(8), reader.GetInt32(9)));
                }
            }
            // 최초 유사학습 시에만 대상 PDF를 추출한다.
            MsdsPdfTextResult? targetPdf = null;
            var extractor = new MsdsPdfTextExtractor();
            var checkedPdf = new Dictionary<string, (bool Valid, bool SameFormat)>();
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (candidate.Hash == hash || already.Any(a => a.Key == candidate.Key) || output.Any(a => a.Key == candidate.Key)) continue;
                if (!MsdsIngredientLearningKey.Parse(candidate.Key, out var cas, out var property)) continue;
                if (property != "Twa" && property != "Stel") continue;
                var ingredients = result.Ingredients.Where(i => i.CasNumber?.Trim() == cas).ToList();
                if (ingredients.Count != 1) continue;
                var before = MsdsIngredientLearningKey.Get(ingredients[0], property) ?? "";
                if (string.IsNullOrWhiteSpace(before) || string.IsNullOrWhiteSpace(candidate.Value) ||
                    string.Equals(before, candidate.Value, StringComparison.Ordinal)) continue;
                // 교정 사례에서 표현 규칙만 학습한다. 대상의 농도·시간 수치는 절대 복사하지 않는다.
                // 교정 원문은 PDF의 주변 근거 텍스트가 아니라, 교정 전 필드값이다.
                // 주변 근거에는 LD50 등 다른 물성이 포함될 수 있으므로 변환 규칙의 입력으로 사용하지 않는다.
                if (!TryApplyExposureRule(before, candidate.Original, candidate.Value, property, out var corrected)) continue;
                if (!System.IO.File.Exists(candidate.Pdf)) continue;
                if (!checkedPdf.TryGetValue(candidate.Pdf, out var check))
                {
                    bool valid = false, sameFormat = false;
                    try
                    {
                        using (var stream = System.IO.File.OpenRead(candidate.Pdf))
                            valid = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)) == candidate.Hash;
                        if (valid)
                        {
                            var sourcePdf = await extractor.ExtractAsync(candidate.Pdf, ct);
                            targetPdf ??= await extractor.ExtractAsync(path, ct);
                            sameFormat = Format(sourcePdf, _ => { }, "학습") && Format(targetPdf, _ => { }, "대상");
                            // 학습 당시의 원문 근거가 원본 PDF에 실제로 있어야 한다.
                            valid = ContainsExposureEvidence(sourcePdf.FullText, candidate.Original);
                            if (!valid) log($"{candidate.Id} 제외: 학습 PDF에 교정 전 노출값 근거 없음");
                        }
                    }
                    catch (System.IO.IOException) { valid = false; }
                    check = (valid, sameFormat);
                    checkedPdf[candidate.Pdf] = check;
                }
                if (!check.Valid) continue;
                targetPdf ??= await extractor.ExtractAsync(path, ct);
                // 해당 CAS가 대상 PDF에 존재하고, 변경 전 TWA/STEL 수치도 원문에 있어야 한다.
                if (!targetPdf.FullText.Contains(cas, StringComparison.Ordinal) ||
                    !ContainsExposureEvidence(targetPdf.FullText, before))
                {
                    log($"{candidate.Id} 제외: 대상 CAS/노출값 근거 없음 ({cas}/{property})");
                    continue;
                }
                bool sameSupplier = Norm(supplier) == Norm(candidate.Supplier);
                bool sameProduct = Norm(product) == Norm(candidate.Product);
                string subtype = sameSupplier
                    ? sameProduct ? (check.SameFormat ? "S01" : "S02") : (check.SameFormat ? "S03" : "S04")
                    : check.SameFormat ? "S05" : "S06";
                MsdsIngredientLearningKey.Set(ingredients[0], property, corrected);
                output.Add(new MsdsLearningEntry {
                    Id = candidate.Id, Key = candidate.Key, Hash = hash, Before = before, After = corrected,
                    Text = before, Page = candidate.Page, Label = "CAS " + cas,
                    Context = "수치·단위 동등성 확인", At = DateTimeOffset.Now.ToString("O"),
                    Type = "SIMILAR", Subtype = subtype
                });
                log($"{subtype} 성분 {cas}/{property}: {before} → {corrected} (원문 검증 필요)");
            }
            return output;
        }
        // 학습 원문과 교정값의 수치가 동일한 경우에만 단위·표현 규칙으로 인정한다.
        // 신규 문서의 수치는 신규 원문에서 가져오며 학습 문서의 수치를 전파하지 않는다.
        private static bool TryApplyExposureRule(string before, string source, string learned,
            string property, out string corrected)
        {
            corrected = "";
            if (!TryExposureParts(source, out var sourceNum, out var sourceUnit, out var sourceTime, out var sourceTimeUnit) ||
                !TryExposureParts(learned, out var learnedNum, out var learnedUnit, out var learnedTime, out var learnedTimeUnit) ||
                !TryExposureParts(before, out var targetNum, out var targetUnit, out var targetTime, out var targetTimeUnit))
                return false;
            if (sourceNum != learnedNum || sourceUnit != learnedUnit ||
                sourceTime != learnedTime || sourceTimeUnit != learnedTimeUnit ||
                targetUnit != learnedUnit || targetTimeUnit != learnedTimeUnit ||
                string.Equals(source.Trim(), learned.Trim(), StringComparison.Ordinal))
                return false;
            // 시간 정보가 원문에 없는 경우 학습 사례의 시간 수치를 가져오지 않는다.
            if (targetTime.Length == 0 && learnedTime.Length != 0) return false;
            var label = property == "Twa" ? "TWA" : "STEL";
            corrected = label + ": " + targetNum + " " + targetUnit +
                (targetTime.Length == 0 ? "" : " " + targetTime + " " + (targetTimeUnit == "H" ? "Hr" : "Min"));
            return !string.Equals(before.Trim(), corrected, StringComparison.Ordinal);
        }
        private static bool TryExposureParts(string value, out string number, out string unit,
            out string time, out string timeUnit)
        {
            number = unit = time = timeUnit = "";
            var m = Regex.Match(value ?? "", @"(?<![\d.])(?<n>\d+(?:[.,]\d+)?)\s*(?<u>ppm|mg\s*/\s*m[³3])\b",
                RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            number = m.Groups["n"].Value.Replace(',', '.');
            unit = Regex.Replace(m.Groups["u"].Value, @"\s+", "").ToUpperInvariant().Replace("M3", "M³");
            var suffix = value.Substring(m.Index + m.Length);
            var t = Regex.Match(suffix, @"(?<t>\d+(?:[.,]\d+)?)\s*(?<u>시간|HOURS?|HRS?|HR|H|분|MINUTES?|MINS?|MIN)\b",
                RegexOptions.IgnoreCase);
            if (t.Success)
            {
                time = t.Groups["t"].Value.Replace(',', '.');
                var u = t.Groups["u"].Value.ToUpperInvariant();
                timeUnit = u == "시간" || u.StartsWith("H") ? "H" : "MIN";
            }
            return true;
        }
        private static string ExposureKey(string value)
        {
            var s = value.Normalize(System.Text.NormalizationForm.FormKC).ToUpperInvariant();
            s = Regex.Replace(s, @"\bHOURS?\b|\bHRS?\b|시간", "H");
            s = Regex.Replace(s, @"\bMINUTES?\b|\bMINS?\b|분", "MIN");
            s = Regex.Replace(s, @"\s+|\.", "");
            return s;
        }
        internal static bool IsSkinClassification(string value) =>
            Regex.IsMatch(value ?? "", @"^\s*피부\s*(?:부식성|자극성)\s*[-–:]\s*분류\s*\d+[A-Za-z]?\s*$", RegexOptions.IgnoreCase);
        internal static bool ContainsSkinClassification(string text, string value)
        {
            if (!IsSkinClassification(value)) return false;
            var match = Regex.Match(value, @"피부\s*(부식성|자극성)\s*[-–:]\s*분류\s*(\d+[A-Za-z]?)", RegexOptions.IgnoreCase);
            return Regex.IsMatch(text ?? "", @"피부\s*" + Regex.Escape(match.Groups[1].Value) +
                @"\s*[-–:]\s*분류\s*" + Regex.Escape(match.Groups[2].Value) + @"(?![\dA-Za-z])", RegexOptions.IgnoreCase);
        }
        // Compatibility API used by existing correction and same-document learning screens.
        internal static bool ExposureEquivalent(string before, string after)
            => EquivalentExposure(before, after);

        private static bool EquivalentExposure(string before, string after)
        {
            if (!Regex.IsMatch(before, @"\d") || !Regex.IsMatch(after, @"\d")) return false;
            if (!Regex.IsMatch(before, @"\b(?:PPM|MG/M[³3])\b", RegexOptions.IgnoreCase)) return false;
            return ExposureKey(before) == ExposureKey(after);
        }
        private static bool ContainsExposureEvidence(string fullText, string value)
        {
            // 문서 추출 과정의 줄바꿈은 허용하되, 수치와 농도 단위가 일치해야 한다.
            var m = Regex.Match(value, @"(?<num>\d+(?:[.,]\d+)?)\s*(?<unit>ppm|mg/m[³3])", RegexOptions.IgnoreCase);
            return m.Success && Regex.IsMatch(fullText,
                Regex.Escape(m.Groups["num"].Value) + @"\s*" + Regex.Escape(m.Groups["unit"].Value),
                RegexOptions.IgnoreCase);
        }

    }
}
