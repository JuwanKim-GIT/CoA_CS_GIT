using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace QCS
{
    // Phase 4: read-only retrieval of confirmed correction examples.
    // This service does not change extracted values, approve learning rules, or write audit history.
    internal static class MsdsSemanticSimilaritySearch
    {
        private const int Dimensions = 3072;
        private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(2) };

        internal sealed class Match
        {
            internal string CorrectionId = "";
            internal string FieldKey = "";
            internal string? CasNumber;
            internal string OriginalValue = "";
            internal string CorrectedValue = "";
            internal string? SupplierName;
            internal string? ProductName;
            internal string TextType = "";
            internal double Similarity;
            internal string EmbeddingText = "";
        }

        // The field key is an exact-match constraint, not a vector feature.
        // Ingredient keys already encode the CAS and item; also verify the persisted CAS.
        internal static async Task<IReadOnlyList<Match>> SearchAsync(
            string apiKey, string fieldKey, string queryText, string? ingredientCasNumber = null,
            int topK = 5, CancellationToken cancellationToken = default, Action<string>? diagnostic = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("OpenAI API 키가 없습니다.");
            if (string.IsNullOrWhiteSpace(fieldKey)) throw new ArgumentException("검색 항목코드가 없습니다.");
            if (string.IsNullOrWhiteSpace(queryText)) return Array.Empty<Match>();
            if (topK < 1 || topK > 50) throw new ArgumentOutOfRangeException(nameof(topK));

            // Compare the same canonical field-value representation on both sides.
            // Legacy SOURCE/CONTEXT embeddings were built from unrelated surrounding sections.
            // Keep them in the DB, but do not use them for scoring.
            var matches = new List<Match>();
            using var db = new SqliteConnection(DatabaseManager.GetConnectionString("msds"));
            await db.OpenAsync(cancellationToken);
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT c.ai_correctionh_id, c.ai_correctionh_field_key,
                       c.ai_correctionh_ingredient_cas_no, c.ai_correctionh_original_value,
                       c.ai_correctionh_corrected_value, c.ai_correctionh_supplier_name,
                       c.ai_correctionh_product_name
                FROM ai_correctionh_hist c
                WHERE c.ai_correctionh_learning_status = 'APPROVED'
                  AND c.ai_correctionh_field_key = $field
                  AND ($cas IS NULL OR c.ai_correctionh_ingredient_cas_no = $cas)";
            cmd.Parameters.AddWithValue("$field", fieldKey.Trim());
            cmd.Parameters.AddWithValue("$cas", string.IsNullOrWhiteSpace(ingredientCasNumber)
                ? DBNull.Value : ingredientCasNumber.Trim());
            using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    matches.Add(new Match
                    {
                        CorrectionId = reader.GetString(0), FieldKey = reader.GetString(1),
                        CasNumber = reader.IsDBNull(2) ? null : reader.GetString(2),
                        OriginalValue = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        CorrectedValue = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        SupplierName = reader.IsDBNull(5) ? null : reader.GetString(5),
                        ProductName = reader.IsDBNull(6) ? null : reader.GetString(6),
                        TextType = "FIELD_VALUE"
                    });
                }
            }
            diagnostic?.Invoke($"[학습 교정 후보] CAS/항목 일치 {matches.Count}건 (기존 문맥 임베딩 사용 안 함)");
            if (matches.Count == 0) return Array.Empty<Match>();
            // The caller provides the current extracted value as the first line of a canonical
            // representation, rather than the ingredient-wide source context.
            diagnostic?.Invoke("[현재 분석 임베딩 입력 시작]\n" + queryText + "\n[현재 분석 임베딩 입력 끝]");
            float[] query = await EmbedAsync(apiKey, queryText, cancellationToken);
            foreach (var match in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string learnedText = CanonicalText(match.FieldKey, match.CasNumber, match.OriginalValue);
                match.EmbeddingText = learnedText;
                float[] learned = await EmbedAsync(apiKey, learnedText, cancellationToken);
                double dot = 0;
                for (int i = 0; i < Dimensions; i++) dot += query[i] * (double)learned[i];
                match.Similarity = Math.Clamp(dot, -1.0, 1.0);
                diagnostic?.Invoke($"[DB 학습 비교] 교정ID={match.CorrectionId} | CAS={match.CasNumber} | 항목={match.FieldKey} | 유형=FIELD_VALUE | 유사도={match.Similarity:F6} | 원본값={match.OriginalValue} | 교정값={match.CorrectedValue}");
                diagnostic?.Invoke("[DB 학습 임베딩 입력 시작]\n" + learnedText + "\n[DB 학습 임베딩 입력 끝]");
            }
            return matches.OrderByDescending(m => m.Similarity)
                .ThenBy(m => m.CorrectionId, StringComparer.Ordinal).Take(topK).ToArray();
        }

        // Cross-CAS retrieval is restricted to approved TWA/STEL expression examples.
        // Chemical measurement corrections must continue through the CAS-specific search.
        internal static async Task<IReadOnlyList<Match>> SearchExpressionExamplesAsync(
            string property, CancellationToken cancellationToken)
        {
            if (property != "Twa" && property != "Stel") return Array.Empty<Match>();
            var matches = new List<Match>();
            using var db = new SqliteConnection(DatabaseManager.GetConnectionString("msds"));
            await db.OpenAsync(cancellationToken);
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT ai_correctionh_id, ai_correctionh_field_key,
                ai_correctionh_ingredient_cas_no, ai_correctionh_original_value,
                ai_correctionh_corrected_value
                FROM ai_correctionh_hist
                WHERE ai_correctionh_learning_status = 'APPROVED'
                  AND ai_correctionh_field_key LIKE $suffix
                ORDER BY ai_correctionh_id DESC LIMIT 100";
            cmd.Parameters.AddWithValue("$suffix", "%:" + property);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                matches.Add(new Match {
                    CorrectionId = reader.GetString(0), FieldKey = reader.GetString(1),
                    CasNumber = reader.IsDBNull(2) ? null : reader.GetString(2),
                    OriginalValue = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    CorrectedValue = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    TextType = "EXPRESSION_PATTERN"
                });
            return matches;
        }

        internal static string CanonicalText(string fieldKey, string? cas, string value)
        {
            // Ignore optional field labels and trailing sentence punctuation, not measurements.
            string v = (value ?? "").Trim();
            int colon = v.IndexOf(':');
            if (colon >= 0 && colon < 16 &&
                (v.Substring(0, colon).Trim().Equals("TWA", StringComparison.OrdinalIgnoreCase) ||
                 v.Substring(0, colon).Trim().Equals("STEL", StringComparison.OrdinalIgnoreCase)))
                v = v.Substring(colon + 1).Trim();
            v = v.TrimEnd('.', ' ', '\r', '\n');
            return $"항목: {fieldKey}\nCAS: {cas?.Trim()}\n값: {v}";
        }

        private static async Task<float[]> EmbedAsync(string apiKey, string text, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/embeddings");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = "text-embedding-3-large", input = text, encoding_format = "float"
            }), Encoding.UTF8, "application/json");
            using var response = await Client.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"OpenAI 유사도 검색 임베딩 오류 {(int)response.StatusCode}: {response.ReasonPhrase}");
            using var json = JsonDocument.Parse(body);
            var data = json.RootElement.GetProperty("data");
            if (data.GetArrayLength() != 1) throw new InvalidOperationException("OpenAI 임베딩 응답 건수가 올바르지 않습니다.");
            var values = data[0].GetProperty("embedding");
            if (values.GetArrayLength() != Dimensions) throw new InvalidOperationException("OpenAI 임베딩 차원이 일치하지 않습니다.");
            var result = new float[Dimensions];
            double norm = 0;
            for (int i = 0; i < Dimensions; i++)
            {
                result[i] = values[i].GetSingle();
                if (float.IsNaN(result[i]) || float.IsInfinity(result[i]))
                    throw new InvalidOperationException("OpenAI 임베딩에 유효하지 않은 숫자가 있습니다.");
                norm += (double)result[i] * result[i];
            }
            if (norm <= 0) throw new InvalidOperationException("OpenAI 임베딩이 빈 벡터입니다.");
            double magnitude = Math.Sqrt(norm);
            for (int i = 0; i < Dimensions; i++) result[i] = (float)(result[i] / magnitude);
            return result;
        }
    }
}
