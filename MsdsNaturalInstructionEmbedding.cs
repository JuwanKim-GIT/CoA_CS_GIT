using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace QCS;

// Generates vectors for saved natural-language instructions.
// OpenAI calls are made outside SQLite transactions (SMB shared DB).
internal static class MsdsNaturalInstructionEmbedding
{
    private const string ModelId = "AE_OPENAI_TEXT_EMBEDDING_3_LARGE_3072_V1";
    private const int Dimensions = 3072;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(2) };

    private static string ReadSetting(string field)
    {
        using var db = new SqliteConnection(DatabaseManager.GetConnectionString("common"));
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT zx_code_cmmt FROM zx_code_mstr WHERE zx_code_fldname=$field AND (zx_code_value IS NULL OR zx_code_value='')";
        cmd.Parameters.AddWithValue("$field", field);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0) || string.IsNullOrWhiteSpace(reader.GetString(0)))
            throw new InvalidOperationException(field + " 공통코드 설정이 없습니다.");
        string value = reader.GetString(0).Trim();
        if (reader.Read()) throw new InvalidOperationException(field + " 공통코드가 중복되어 있습니다.");
        return value;
    }

    internal static bool Reconnect(SqliteConnection db, SqliteTransaction tx, string id, string description)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(description.Trim())));
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = @"INSERT OR IGNORE INTO ai_instruction_vectord_det
(ai_instruction_vector_learning_id,ai_instruction_vector_embedding_id,ai_instruction_vector_text_hash,ai_instruction_vector_vector,ai_instruction_vector_created_at)
SELECT $id,ai_instruction_vector_embedding_id,ai_instruction_vector_text_hash,ai_instruction_vector_vector,ai_instruction_vector_created_at FROM ai_instruction_vectord_det
WHERE ai_instruction_vector_embedding_id=$model AND ai_instruction_vector_text_hash=$hash AND length(ai_instruction_vector_vector)=12288 LIMIT 1";
        cmd.Parameters.AddWithValue("$id",id); cmd.Parameters.AddWithValue("$model",ModelId); cmd.Parameters.AddWithValue("$hash",hash); cmd.ExecuteNonQuery();
        cmd.CommandText = @"UPDATE ai_learning_mstr SET ai_learning_embedding_id=$model,ai_learning_text_hash=$hash WHERE ai_learning_id=$id AND EXISTS (SELECT 1 FROM ai_instruction_vectord_det WHERE ai_instruction_vector_learning_id=$id AND ai_instruction_vector_embedding_id=$model AND ai_instruction_vector_text_hash=$hash AND length(ai_instruction_vector_vector)=12288)";
        return cmd.ExecuteNonQuery() == 1;
    }

    internal static async Task<string> GenerateAsync(string instructionId, string description, CancellationToken ct = default)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(description)));
        using (var db = new SqliteConnection(DatabaseManager.GetConnectionString("msds")))
        {
            await db.OpenAsync(ct);
            using var tx = db.BeginTransaction();
            using var verify = db.CreateCommand(); verify.Transaction = tx;
            verify.CommandText = "SELECT ai_learning_description FROM ai_learning_mstr WHERE ai_learning_id=$id";
            verify.Parameters.AddWithValue("$id",instructionId);
            if (!string.Equals(Convert.ToString(verify.ExecuteScalar())?.Trim(), description, StringComparison.Ordinal)) throw new InvalidOperationException("지침이 변경되었습니다.");
            bool reused = Reconnect(db,tx,instructionId,description); tx.Commit();
            if (reused) return "기존 임베딩 연결 완료";
        }
        string model = ReadSetting("API_EMBEDDING");
        if (!string.Equals(model, "text-embedding-3-large", StringComparison.Ordinal))
            throw new InvalidOperationException("현재 임베딩 저장 구조는 text-embedding-3-large만 지원합니다. 모델 전환은 전체 재임베딩 절차가 필요합니다.");
        string key = ReadSetting("MSDS_API_KEY");
        string payload = JsonSerializer.Serialize(new { model, input = description, encoding_format = "float" });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/embeddings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request, ct);
        string json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI 임베딩 API 오류 {(int)response.StatusCode}: {response.ReasonPhrase}");
        float[] vector = new float[Dimensions];
        using (var document = JsonDocument.Parse(json))
        {
            var data = document.RootElement.GetProperty("data");
            if (data.GetArrayLength() != 1) throw new InvalidOperationException("임베딩 응답 건수 오류");
            var values = data[0].GetProperty("embedding");
            if (values.GetArrayLength() != Dimensions) throw new InvalidOperationException("임베딩 차원 오류");
            double sum = 0;
            for (int i = 0; i < Dimensions; i++)
            {
                float value = values[i].GetSingle();
                if (!float.IsFinite(value)) throw new InvalidOperationException("잘못된 임베딩 벡터");
                vector[i] = value; sum += (double)value * value;
            }
            if (sum <= 0) throw new InvalidOperationException("빈 임베딩 벡터");
            double norm = Math.Sqrt(sum);
            for (int i = 0; i < Dimensions; i++) vector[i] = (float)(vector[i] / norm);
        }
        byte[] bytes = new byte[Dimensions * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        using (var db = new SqliteConnection(DatabaseManager.GetConnectionString("msds")))
        {
            await db.OpenAsync(ct);
            using var tx = db.BeginTransaction();
            // Reject stale API responses when an instruction was changed while embedding was running.
            using (var verify = db.CreateCommand())
            {
                verify.Transaction = tx;
                verify.CommandText = "SELECT ai_learning_description FROM ai_learning_mstr WHERE ai_learning_id=$id";
                verify.Parameters.AddWithValue("$id", instructionId);
                string latest = Convert.ToString(verify.ExecuteScalar())?.Trim() ?? "";
                if (latest != description) throw new InvalidOperationException("임베딩 처리 중 지침이 변경되었습니다. 다시 실행하세요.");
            }
            using (var insert = db.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = @"INSERT OR IGNORE INTO ai_instruction_vectord_det
(ai_instruction_vector_learning_id,ai_instruction_vector_embedding_id,ai_instruction_vector_text_hash,ai_instruction_vector_vector,ai_instruction_vector_created_at)
VALUES ($id,$model,$hash,$vector,$at)";
                insert.Parameters.AddWithValue("$id", instructionId);
                insert.Parameters.AddWithValue("$model", ModelId);
                insert.Parameters.AddWithValue("$hash", hash);
                insert.Parameters.AddWithValue("$vector", bytes);
                insert.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
                insert.ExecuteNonQuery();
            }
            using (var update = db.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = @"UPDATE ai_learning_mstr SET ai_learning_embedding_id=$model,
ai_learning_text_hash=$hash,ai_learning_updated_at=$at WHERE ai_learning_id=$id";
                update.Parameters.AddWithValue("$id", instructionId);
                update.Parameters.AddWithValue("$model", ModelId);
                update.Parameters.AddWithValue("$hash", hash);
                update.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
                update.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return $"임베딩 생성 및 저장 완료 ({model}, {Dimensions}차원)";
    }
}
