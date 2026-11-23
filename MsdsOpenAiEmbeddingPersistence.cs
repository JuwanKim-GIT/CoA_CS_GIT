using System;
using System.Collections.Generic;
using System.Globalization;
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
    // Semantic embeddings are stored under a distinct model ID; legacy character vectors are retained.
    // Network requests happen before the SQLite write transaction, never while holding an SMB DB lock.
    internal static class MsdsOpenAiEmbeddingPersistence
    {
        internal const string ModelId = "AE_OPENAI_TEXT_EMBEDDING_3_LARGE_3072_V1";
        private const string ModelName = "text-embedding-3-large";
        private const int Dimensions = 3072;
        private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(2) };

        internal static async Task<int> SyncAsync(string apiKey, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("OpenAI API 키가 없습니다.");
            var pending = new List<(string CorrectionId, string Type, string Text, string Hash)>();
            using (var db = new SqliteConnection(DatabaseManager.GetConnectionString("msds")))
            {
                await db.OpenAsync(ct);
                using var cmd = db.CreateCommand();
                // Text selection follows the existing lexical vector implementation.
                cmd.CommandText = @"SELECT c.ai_correctionh_id, 'SOURCE',
                    COALESCE(NULLIF(c.ai_correctionh_source_text,''),c.ai_correctionh_original_value,'')
                    FROM ai_correctionh_hist c WHERE c.ai_correctionh_learning_status='APPROVED'
                    AND NOT EXISTS (SELECT 1 FROM ai_embeddingd_det e WHERE e.ai_embeddingd_correction_id=c.ai_correctionh_id
                        AND e.ai_embeddingd_embedding_id=$model AND e.ai_embeddingd_text_type='SOURCE')
                    UNION ALL
                    SELECT c.ai_correctionh_id, 'CONTEXT', COALESCE(c.ai_correctionh_source_context,'')
                    FROM ai_correctionh_hist c WHERE c.ai_correctionh_learning_status='APPROVED'
                    AND NOT EXISTS (SELECT 1 FROM ai_embeddingd_det e WHERE e.ai_embeddingd_correction_id=c.ai_correctionh_id
                        AND e.ai_embeddingd_embedding_id=$model AND e.ai_embeddingd_text_type='CONTEXT')";
                cmd.Parameters.AddWithValue("$model", ModelId);
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    string value = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    if (!string.IsNullOrWhiteSpace(value))
                        pending.Add((reader.GetString(0), reader.GetString(1), value,
                            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)))));
                }
            }
            int inserted = 0;
            // Request one embedding per text to keep the saved evidence and vector mapping unambiguous.
            foreach (var item in pending)
            {
                ct.ThrowIfCancellationRequested();
                var body = JsonSerializer.Serialize(new { model = ModelName, input = item.Text, encoding_format = "float" });
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/embeddings");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await Client.SendAsync(request, ct);
                string json = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"OpenAI 임베딩 API 오류 {(int)response.StatusCode}: {response.ReasonPhrase}");
                using var parsed = JsonDocument.Parse(json);
                var data = parsed.RootElement.GetProperty("data");
                if (data.GetArrayLength() != 1) throw new InvalidOperationException("OpenAI 임베딩 응답 건수가 올바르지 않습니다.");
                var embedding = data[0].GetProperty("embedding");
                if (embedding.GetArrayLength() != Dimensions) throw new InvalidOperationException("OpenAI 임베딩 차원이 일치하지 않습니다.");
                var vector = new float[Dimensions];
                double magnitude = 0;
                for (int i = 0; i < Dimensions; i++)
                {
                    vector[i] = embedding[i].GetSingle();
                    if (float.IsNaN(vector[i]) || float.IsInfinity(vector[i])) throw new InvalidOperationException("잘못된 임베딩 벡터입니다.");
                    magnitude += (double)vector[i] * vector[i];
                }
                if (magnitude <= 0) throw new InvalidOperationException("빈 임베딩 벡터입니다.");
                var norm = Math.Sqrt(magnitude);
                for (int i = 0; i < Dimensions; i++) vector[i] = (float)(vector[i] / norm);
                var bytes = new byte[Dimensions * sizeof(float)];
                Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
                using var db = new SqliteConnection(DatabaseManager.GetConnectionString("msds"));
                await db.OpenAsync(ct);
                using var tx = db.BeginTransaction();
                using (var model = db.CreateCommand())
                {
                    model.Transaction = tx;
                    model.CommandText = @"INSERT OR IGNORE INTO ai_embedding_mstr
                        (ai_embedding_id,ai_embedding_model_name,ai_embedding_model_version,
                         ai_embedding_dimensions,ai_embedding_metric,ai_embedding_normalized,ai_embedding_created_at)
                        VALUES ($id,$name,'1',3072,'COSINE',1,$at)";
                    model.Parameters.AddWithValue("$id", ModelId);
                    model.Parameters.AddWithValue("$name", ModelName);
                    model.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
                    model.ExecuteNonQuery();
                }
                using (var insert = db.CreateCommand())
                {
                    insert.Transaction = tx;
                    insert.CommandText = @"INSERT OR IGNORE INTO ai_embeddingd_det
                        (ai_embeddingd_embedding_id,ai_embeddingd_correction_id,ai_embeddingd_text_type,
                         ai_embeddingd_text,ai_embeddingd_text_hash,ai_embeddingd_vector,ai_embeddingd_created_at)
                        VALUES ($model,$correction,$type,$text,$hash,$vector,$at)";
                    insert.Parameters.AddWithValue("$model", ModelId);
                    insert.Parameters.AddWithValue("$correction", item.CorrectionId);
                    insert.Parameters.AddWithValue("$type", item.Type);
                    insert.Parameters.AddWithValue("$text", item.Text);
                    insert.Parameters.AddWithValue("$hash", item.Hash);
                    insert.Parameters.AddWithValue("$vector", bytes);
                    insert.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
                    inserted += insert.ExecuteNonQuery();
                }
                tx.Commit();
            }
            return inserted;
        }
    }
}
