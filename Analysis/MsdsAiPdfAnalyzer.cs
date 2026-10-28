using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
            "msds_lel", "msds_uel", "msds_hazard_codes"
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
                "ingredients must contain {name: string|null, cas_number: string|null, concentration_raw: string|null, source_page: integer|null, source_text: string|null}. " +
                "Use exact source evidence, do not infer or invent missing data. Use null when absent. " +
                "Keep supplier and importer distinct; supplier is the document's supplier/manufacturer. " +
                "Do not confuse first issue date with revision date. Do not merge ingredients sharing CAS unless document explicitly does. " +
                "For H codes use comma-separated codes; if document explicitly says not classified, return that wording. " +
                "Preserve 'not applicable', 'not available', and 'not classified' literally instead of null when stated. " +
                "For revision dates use YYYY-MM-DD only if unambiguous. Respond in original document language.";

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
            var result = new MsdsAnalysisResult { FilePath = pdfPath, Engine = MsdsEngineMode.AiDirect };
            var data = parsed.RootElement;
            if (!data.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("AI 응답의 fields 구조가 올바르지 않습니다.");
            foreach (var key in FieldKeys)
            {
                if (!fields.TryGetProperty(key, out var field)) continue;
                string? value = field.ValueKind == JsonValueKind.Object ? ReadString(field, "value") : field.ToString();
                if (string.IsNullOrWhiteSpace(value)) continue;
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
