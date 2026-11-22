using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QCS.Analysis
{
    // PDF 원본을 Responses API에 직접 전달한다. 기존 C# PDF 파서와 독립적이다.
    public sealed class MsdsAiPdfAnalyzer
    {
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromMinutes(6) };
        private static readonly string[] FieldKeys = {
            "msds_product_name", "msds_supplier_name", "msds_sds_code", "msds_supplier_phone",
            "msds_supplier_address", "msds_revision_date", "msds_revision_number",
            "msds_un_number", "msds_flash_point", "msds_autoignition", "msds_vapor_pressure",
            "msds_moel_submission_no", "msds_lel", "msds_uel", "msds_hazard_codes", "msds_ppe", "msds_molecular_formula", "msds_physical_state", "msds_hazardous_goods_class", "msds_reactivity"
        };

        private readonly string _apiKey;
        private readonly string _model;

        public MsdsAiPdfAnalyzer(string apiKey, string model)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API Key가 비어 있습니다.");
            if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("AI 모델이 비어 있습니다.");
            _apiKey = apiKey.Trim();
            _model = model.Trim();
        }

        public async Task<MsdsAnalysisResult> AnalyzeAsync(string pdfPath, CancellationToken ct = default)
        {
            if (!File.Exists(pdfPath)) throw new FileNotFoundException("PDF를 찾을 수 없습니다.", pdfPath);
            byte[] bytes = await File.ReadAllBytesAsync(pdfPath, ct);
            if (bytes.Length > 45 * 1024 * 1024)
                throw new InvalidOperationException("PDF 크기가 45MB를 초과합니다. 파일을 나누어 분석하세요.");

            
            string prompt = "You are an MSDS/SDS extraction assistant. Read the attached PDF directly. " +
                "Return ONLY a JSON object with keys 'fields' (object), 'ingredients' (array), and 'warnings' (array of strings). " +
                "For each of these field keys provide an object {value: string|null, source_page: integer|null, source_text: string|null}: " +
                string.Join(", ", FieldKeys) + ". " +
                "ingredients must contain {name: string|null, cas_number: string|null, concentration_raw: string|null, twa: string|null, stel: string|null, oral_ld50: string|null, dermal_ld50: string|null, inhalation_lc50: string|null, source_page: integer|null, source_text: string|null}. " +
                "Use exact source evidence, do not infer or invent missing data. Use null when absent. " +
                "Keep supplier and importer distinct; supplier is the document's supplier/manufacturer. " +
                "Do not confuse first issue date with revision date. Do not merge ingredients sharing CAS unless document explicitly does. Enumerate every section 3 ingredient across page breaks, even if toxicity and exposure data are absent. Korean label 제품 등록 번호 is an MSDS submission number when explicitly printed. " +
                "For H codes use comma-separated codes; if document explicitly says not classified, return that wording. " +
                "Preserve 'not applicable', 'not available', and 'not classified' literally instead of null when stated. " +
                "Preserve revision date and revision number exactly as printed. For each ingredient match section 8 TWA/STEL and section 11 oral LD50, dermal LD50, inhalation LC50 by explicit identity; never use mixture values or other routes. Retain all reported values, species, units, inequalities and exposure times verbatim. Keep multiple results in one string separated by newlines. Do not convert units or infer missing values. Extract physical state and molecular formula from explicit product information, hazardous goods classification from section 15, and reactivity as original section 10 wording, not a yes/no judgment. For supplier phone use the ordinary supplier Tel in section 1 only; never substitute emergency phone or fax. For msds_ppe extract explicit personal protective equipment in section 8. Molecular formula must be explicitly a molecular formula, never molecular weight. Hazardous goods classification must be the Korean Dangerous Substances Safety Management Act entry in section 15, never transport class from section 14. Reactivity must be the explicit chemical stability and hazardous reaction entry, not conditions to avoid; do not infer a boolean. For oral LD50 allow only oral (경구); exclude intraperitoneal (복강내), subcutaneous (피하), intravenous and other routes. For dermal LD50 allow only dermal (경피); exclude subcutaneous injection. For inhalation LC50 allow only explicit inhalation LC50. If no matching route exists return null. Check every returned toxicity line against its route and ingredient before returning JSON. Extract msds_moel_submission_no only from an explicitly labelled MSDS submission or product registration number in the document, never the filename or SDS code. If absent return null. For molecular formula return null if only molecular weight is present. Include every item of the section 15 dangerous goods entry including threshold quantity (역치). Respond in original document language.";

            var payload = new {
                model = _model,
                input = new object[] { new {
                    role = "user",
                    content = new object[] {
                        new { type = "input_text", text = prompt },
                        new { type = "input_file", filename = Path.GetFileName(pdfPath), file_data = "data:application/pdf;base64," + Convert.ToBase64String(bytes) }
                    }
                } },
                text = new { format = new { type = "json_object" } }
            };
            return await SendAndParseAsync(payload, pdfPath, MsdsEngineMode.AiDirect, ct);
        }

        // Hybrid: send only locally extracted text; never attach the PDF file.
        public async Task<MsdsAnalysisResult> AnalyzeTextAsync(string pdfPath, string fullText, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(fullText))
                throw new InvalidOperationException("PDF에서 추출된 텍스트가 없습니다.");

            string prompt = "You are an MSDS/SDS extraction assistant. Analyze the FULL PDF text provided below, not an attached PDF. " +
                "Page markers [PAGE N] indicate source pages. Text may have imperfect table layout. " +
                "Return ONLY a JSON object with keys 'fields' (object), 'ingredients' (array), and 'warnings' (array of strings). " +
                "For each of these field keys provide an object {value: string|null, source_page: integer|null, source_text: string|null}: " +
                string.Join(", ", FieldKeys) + ". " +
                "ingredients must contain {name: string|null, cas_number: string|null, concentration_raw: string|null, twa: string|null, stel: string|null, oral_ld50: string|null, dermal_ld50: string|null, inhalation_lc50: string|null, source_page: integer|null, source_text: string|null}. " +
                "Use exact source evidence, do not infer or invent missing data. Use null when absent. " +
                "Keep supplier and importer distinct; supplier is the document's supplier/manufacturer. " +
                "Do not confuse first issue date with revision date. Do not merge ingredients sharing CAS unless document explicitly does. Enumerate every section 3 ingredient across page breaks, even if toxicity and exposure data are absent. Korean label 제품 등록 번호 is an MSDS submission number when explicitly printed. " +
                "For H codes use comma-separated codes; if document explicitly says not classified, return that wording. " +
                "Preserve 'not applicable', 'not available', and 'not classified' literally instead of null when stated. " +
                "Preserve revision date and revision number exactly as printed. For each ingredient match section 8 TWA/STEL and section 11 oral LD50, dermal LD50, inhalation LC50 by explicit identity; never use mixture values or other routes. Retain all reported values, species, units, inequalities and exposure times verbatim. Keep multiple results in one string separated by newlines. Do not convert units or infer missing values. Extract physical state and molecular formula from explicit product information, hazardous goods classification from section 15, and reactivity as original section 10 wording, not a yes/no judgment. For supplier phone use the ordinary supplier Tel in section 1 only; never substitute emergency phone or fax. For msds_ppe extract explicit personal protective equipment in section 8. Molecular formula must be explicitly a molecular formula, never molecular weight. Hazardous goods classification must be the Korean Dangerous Substances Safety Management Act entry in section 15, never transport class from section 14. Reactivity must be the explicit chemical stability and hazardous reaction entry, not conditions to avoid; do not infer a boolean. For oral LD50 allow only oral (경구); exclude intraperitoneal (복강내), subcutaneous (피하), intravenous and other routes. For dermal LD50 allow only dermal (경피); exclude subcutaneous injection. For inhalation LC50 allow only explicit inhalation LC50. If no matching route exists return null. Check every returned toxicity line against its route and ingredient before returning JSON. Extract msds_moel_submission_no only from an explicitly labelled MSDS submission or product registration number in the document, never the filename or SDS code. If absent return null. For molecular formula return null if only molecular weight is present. Include every item of the section 15 dangerous goods entry including threshold quantity (역치). Respond in original document language.";

            var payload = new {
                model = _model,
                input = new object[] { new {
                    role = "user",
                    content = new object[] {
                        new { type = "input_text", text = prompt + "\n\n=== BEGIN MSDS TEXT ===\n" + fullText + "\n=== END MSDS TEXT ===" }
                    }
                } },
                text = new { format = new { type = "json_object" } }
            };
            var result = await SendAndParseAsync(payload, pdfPath, MsdsEngineMode.AiDirect, ct);
            // Section 3 CAS coverage is checked independently of the AI response.
            var section = Regex.Match(fullText, @"(?s)(?:^|\n)\s*3\s*[.．]\s*(?:구성\s*성분|성분의|Composition).*?(?=(?:^|\n)\s*4\s*[.．]\s*(?:응급|First\s*aid))", RegexOptions.IgnoreCase);
            if (section.Success)
            {
                var expected = Regex.Matches(section.Value, @"(?<!\d)\d{2,7}-\d{2}-\d(?!\d)").Cast<Match>().Select(m => m.Value).Distinct().ToArray();
                var missing = expected.Where(c => !result.Ingredients.Any(i => string.Equals(i.CasNumber?.Trim(), c, StringComparison.Ordinal))).ToArray();
                if (missing.Length > 0)
                {
                    // Retry once with explicit missing CAS identifiers, not previous document values.
                    var retryPayload = new { model = _model, input = new object[] { new { role = "user", content = new object[] { new { type = "input_text", text = prompt + "\nReturn the COMPLETE document result. Section 3 includes these CAS identifiers; do not omit them: " + string.Join(", ", expected) + "\n" + fullText } } } }, text = new { format = new { type = "json_object" } } };
                    MsdsAnalysisResult retry;
                    try { retry = await SendAndParseAsync(retryPayload, pdfPath, MsdsEngineMode.AiDirect, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception) { retry = new MsdsAnalysisResult(); result.Warnings.Add("구성성분 재분석 실패: 원문 확인 필요"); }
                    foreach (var c in missing)
                    {
                        var recovered = retry.Ingredients.Where(i => string.Equals(i.CasNumber?.Trim(), c, StringComparison.Ordinal)).ToArray();
                        if (recovered.Length > 0) result.Ingredients.AddRange(recovered);
                        else result.Warnings.Add("구성성분 누락 확인 필요: CAS " + c);
                    }
                    // Keep the original fields and existing ingredient rows unchanged.
                }
            }
            else result.Warnings.Add("3항 범위 자동 식별 실패: 구성성분 누락 여부 수동 확인 필요");
            var number = Regex.Matches(fullText, @"(?:제품\s*등록\s*번호|(?:MSDS|물질안전보건자료)\s*(?:제출|등록)\s*번호|노동부\s*제출\s*번호)\s*[:：]?\s*([A-Z]{2}\d{5}-\d{10})(?![0-9])", RegexOptions.IgnoreCase).Cast<Match>().ToArray();
            if (number.Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            {
                var m = number[0];
                var page = Regex.Matches(fullText.Substring(0, m.Index), @"\[PAGE (\d+)\]").Cast<Match>().LastOrDefault();
                result.Fields.RemoveAll(f => f.FieldKey == "msds_moel_submission_no");
                result.Fields.Add(new MsdsExtractedField { FieldKey = "msds_moel_submission_no", Value = m.Groups[1].Value, SourceText = m.Value, SourcePage = page == null ? (int?)null : int.Parse(page.Groups[1].Value), Status = MsdsFieldStatus.NeedsReview });
            }
            else if (number.Length > 0) result.Warnings.Add("MSDS 제출번호 후보 복수: 원문 확인 필요");
            return result;
        }

        private async Task<MsdsAnalysisResult> SendAndParseAsync(object payload, string pdfPath, MsdsEngineMode engine, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await Client.SendAsync(request, ct);
            string responseBody = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"AI API 오류 {(int)response.StatusCode}: {SafeError(responseBody).Replace(_apiKey, "[REDACTED]")}");
            using var envelope = JsonDocument.Parse(responseBody);
            var root = envelope.RootElement;
            if (root.TryGetProperty("status", out var status) && status.GetString() != "completed")
                throw new InvalidOperationException("AI 응답이 완료되지 않았습니다: " + status.ToString());
            var texts = new List<string>();
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                foreach (var item in output.EnumerateArray())
                    if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        foreach (var block in content.EnumerateArray())
                            if (block.TryGetProperty("type", out var type) && type.GetString() == "output_text"
                                && block.TryGetProperty("text", out var txt)) texts.Add(txt.GetString() ?? "");
            if (texts.Count == 0) throw new InvalidOperationException("AI 응답에 분석 JSON이 없습니다.");
            string json = string.Join("\n", texts);
            using var parsed = JsonDocument.Parse(json);
            var result = new MsdsAnalysisResult { FilePath = pdfPath, Engine = engine };
            var data = parsed.RootElement;
            if (!data.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("AI 응답의 fields 구조가 올바르지 않습니다.");
            foreach (var key in FieldKeys)
            {
                if (!fields.TryGetProperty(key, out var field)) continue;
                string? value = field.ValueKind == JsonValueKind.Object ? ReadString(field, "value") : field.ToString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (key == "msds_molecular_formula" && (ReadString(field, "source_text") ?? "").Contains("분자량") && !(ReadString(field, "source_text") ?? "").Contains("분자식")) continue;
                result.Fields.Add(new MsdsExtractedField {
                    FieldKey = key, Value = value, Status = MsdsFieldStatus.NeedsReview,
                    SourcePage = ReadInt(field, "source_page"), SourceText = ReadString(field, "source_text")
                });
            }
            if (data.TryGetProperty("ingredients", out var ingredients) && ingredients.ValueKind == JsonValueKind.Array)
                foreach (var ing in ingredients.EnumerateArray())
                {
                    if (ing.ValueKind != JsonValueKind.Object) continue;
                    result.Ingredients.Add(new MsdsIngredient {
                        Name = ReadString(ing, "name"), CasNumber = ReadString(ing, "cas_number"),
                        ConcentrationRaw = ReadString(ing, "concentration_raw"),
                        Twa = ReadString(ing, "twa"),
                        Stel = ReadString(ing, "stel"),
                        OralLd50 = ReadString(ing, "oral_ld50"),
                        DermalLd50 = ReadString(ing, "dermal_ld50"),
                        InhalationLc50 = ReadString(ing, "inhalation_lc50"),

                        SourcePage = ReadInt(ing, "source_page"), SourceText = ReadString(ing, "source_text")
                    });
                }
            if (data.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array)
                foreach (var w in warnings.EnumerateArray()) if (w.ValueKind == JsonValueKind.String) result.Warnings.Add(w.GetString() ?? "");
            return result;
        }

        private static string? ReadString(JsonElement obj, string name) =>
            obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
                ? v.ToString() : null;
        private static int? ReadInt(JsonElement obj, string name) =>
            obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;
        private static string SafeError(string body) => body.Length <= 700 ? body : body.Substring(0, 700);
    }
}
