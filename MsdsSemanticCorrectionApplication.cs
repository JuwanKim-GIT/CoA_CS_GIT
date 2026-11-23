using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using QCS.Analysis;

namespace QCS
{
    // Retrieval-augmented correction: only the matching CAS/field may be considered.
    // No database writes or PDF content changes occur during analysis.
    internal static class MsdsSemanticCorrectionApplication
    {
        private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(2) };
        internal sealed class Proposal
        {
            internal int Order;
            internal string Cas = "", Property = "", Before = "", After = "", CorrectionId = "";
            internal double Similarity;
        }
        internal static async Task<IReadOnlyList<Proposal>> ProposeAsync(string apiKey, MsdsAnalysisResult result, CancellationToken ct, Action<string>? diagnostic = null)
        {
            var proposals = new List<Proposal>();
            int searched = 0, matched = 0, verified = 0;
            diagnostic?.Invoke($"교정 진단 시작: 성분 {result.Ingredients.Count}건");
            foreach (var pair in result.Ingredients.Select((ingredient, index) => (ingredient, index)))
            {
                ct.ThrowIfCancellationRequested();
                var ingredient = pair.ingredient;
                string cas = ingredient.CasNumber?.Trim() ?? "";
                if (cas.Length == 0 || result.Ingredients.Count(x => x.CasNumber?.Trim() == cas) != 1) continue;
                foreach (string property in MsdsIngredientLearningKey.Fields)
                {
                    string before = MsdsIngredientLearningKey.Get(ingredient, property)?.Trim() ?? "";
                    if (before.Length == 0) continue;
                    var key = MsdsIngredientLearningKey.Key(cas, property);
                    searched++;
                    IReadOnlyList<MsdsSemanticSimilaritySearch.Match> matches;
                    try { matches = await MsdsSemanticSimilaritySearch.SearchAsync(apiKey, key,
                        MsdsSemanticSimilaritySearch.CanonicalText(key, cas, before),
                        cas, 3, ct, diagnostic); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        diagnostic?.Invoke($"검색 실패 CAS={cas} 항목={property}: {ex.GetType().Name}: {ex.Message}");
                        continue;
                    }
                    diagnostic?.Invoke($"검색 결과 CAS={cas} 항목={property}: {matches.Count}건" +
                        (matches.Count > 0 ? $", 최고 유사도={matches.Max(m => m.Similarity):F3}" : ""));
                    // One correction pipeline: exact confirmed examples are a special case of retrieval.
                    var exact = matches.Where(m =>
                        string.Equals(MsdsSemanticSimilaritySearch.CanonicalText(key, cas, m.OriginalValue),
                            MsdsSemanticSimilaritySearch.CanonicalText(key, cas, before), StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(m.CorrectedValue) &&
                        !string.Equals(m.CorrectedValue.Trim(), before, StringComparison.Ordinal)).ToArray();
                    if (exact.Length > 0)
                    {
                        var distinct = exact.Select(m => m.CorrectedValue.Trim()).Distinct(StringComparer.Ordinal).ToArray();
                        if (distinct.Length != 1)
                        {
                            diagnostic?.Invoke($"정확 일치 제외 CAS={cas} 항목={property}: 확정 교정값 충돌 {distinct.Length}종");
                            continue;
                        }
                        var known = exact[0];
                        string corrected = distinct[0];
                        if (!SameNumbers(before, corrected))
                        {
                            diagnostic?.Invoke($"정확 일치 제외 CAS={cas} 항목={property}: 숫자 일치 검증 실패");
                            continue;
                        }
                        matched++;
                        verified++;
                        diagnostic?.Invoke($"정확 일치 교정 CAS={cas} 항목={property}: 확정 학습값 재사용 | 유사도={known.Similarity:F3}");
                        proposals.Add(new Proposal { Order = pair.index + 1, Cas = cas, Property = property,
                            Before = before, After = corrected, CorrectionId = known.CorrectionId, Similarity = known.Similarity });
                        continue;
                    }
                    var candidate = matches.FirstOrDefault(m => m.Similarity >= 0.72 &&
                        !string.IsNullOrWhiteSpace(m.CorrectedValue) &&
                        !string.Equals(m.CorrectedValue.Trim(), before, StringComparison.Ordinal));
                    if (candidate == null) { diagnostic?.Invoke($"유사 후보 제외 CAS={cas} 항목={property}: 기준 0.72 미달 또는 유효한 교정값 없음"); continue; }
                    matched++;
                    string? after;
                    try { after = await VerifyAsync(apiKey, property, before, ingredient.SourceText ?? "", candidate, ct, diagnostic, cas); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { diagnostic?.Invoke($"AI 검증 실패 CAS={cas} 항목={property}: {ex.GetType().Name}: {ex.Message}"); continue; }
                    if (after == null) continue;
                    verified++;
                    if (after == before) { diagnostic?.Invoke($"적용 제외 CAS={cas} 항목={property}: 기존 값과 동일"); continue; }
                    if (!SameNumbers(before, after)) { diagnostic?.Invoke($"적용 제외 CAS={cas} 항목={property}: 숫자 일치 검증 실패"); continue; }
                    diagnostic?.Invoke($"유사 교정 제안 CAS={cas} 항목={property}: 적용 가능");
                    proposals.Add(new Proposal { Order = pair.index + 1, Cas = cas, Property = property,
                        Before = before, After = after, CorrectionId = candidate.CorrectionId, Similarity = candidate.Similarity });
                }
            }
            diagnostic?.Invoke($"교정 진단 종료: 검색 {searched}건, 후보 {matched}건, AI 제안 {verified}건, 적용 가능 {proposals.Count}건");
            return proposals;
        }
        private static bool SameNumbers(string a, string b)
        {
            static string[] Numbers(string text) => Regex.Matches(text, @"(?<![\w])\d+(?:[.,]\d+)?")
                .Select(m => m.Value.Replace(',', '.')).OrderBy(v => v, StringComparer.Ordinal).ToArray();
            return Numbers(a).SequenceEqual(Numbers(b), StringComparer.Ordinal);
        }
        private static async Task<string?> VerifyAsync(string apiKey, string property, string original,
            string sourceText, MsdsSemanticSimilaritySearch.Match example, CancellationToken ct, Action<string>? diagnostic, string cas)
        {
            var prompt = $"현재 MSDS의 {property} 값을 검증하십시오. 학습 사례는 형식 참고용이며, 과거 값을 그대로 복사하면 안 됩니다. " +
                "현재 값에 있는 모든 숫자, 단위, 노출시간, 비교기호, 경로를 그대로 유지한 표현 정규화만 허용합니다. " +
                "사용자가 확정한 학습 사례의 표기 규칙을 현재 값에 적용할 수 있으면 apply=true로 제안하십시오. " +
                "의미나 측정값이 달라지거나 근거가 불충분하면 apply=false로 반환하십시오. " +
                "JSON 객체 {\"value\":\"...\",\"apply\":false} 형식만 반환하십시오.\n" +
                $"현재 값: {original}\n현재 원문: {sourceText}\n" +
                $"학습 사례 이전 값: {example.OriginalValue}\n학습 사례 사용자 확정 값: {example.CorrectedValue}";
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            req.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = "gpt-4.1-mini", temperature = 0,
                response_format = new { type = "json_object" },
                messages = new[] { new { role = "system", content = "You validate MSDS field normalization. Never invent or alter chemical measurements." },
                    new { role = "user", content = prompt } }
            }), Encoding.UTF8, "application/json");
            using var response = await Client.SendAsync(req, ct);
            if (!response.IsSuccessStatusCode)
            {
                diagnostic?.Invoke($"AI API 오류 CAS={cas} 항목={property}: HTTP {(int)response.StatusCode}");
                return null;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content))
            {
                diagnostic?.Invoke($"AI 응답 오류 CAS={cas} 항목={property}: 빈 응답");
                return null;
            }
            using var answer = JsonDocument.Parse(content);
            if (!answer.RootElement.TryGetProperty("apply", out var apply) ||
                (apply.ValueKind != JsonValueKind.True && apply.ValueKind != JsonValueKind.False) ||
                !answer.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String)
            {
                diagnostic?.Invoke($"AI 응답 형식 오류 CAS={cas} 항목={property}: apply/value 필드 누락 또는 형식 불일치");
                return null;
            }
            if (apply.ValueKind == JsonValueKind.False)
            {
                diagnostic?.Invoke($"AI 교정 거부 CAS={cas} 항목={property}: 모델이 apply=false 반환");
                return null;
            }
            string proposed = value.GetString()?.Trim() ?? "";
            if (proposed.Length == 0 || proposed.Length >= 2000)
            {
                diagnostic?.Invoke($"AI 응답 형식 오류 CAS={cas} 항목={property}: 교정값 길이 오류");
                return null;
            }
            diagnostic?.Invoke($"AI 교정 승인 CAS={cas} 항목={property}: 교정값 반환");
            return proposed;
        }
    }
}
