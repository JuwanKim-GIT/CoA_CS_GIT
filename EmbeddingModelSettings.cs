using System;
using Microsoft.Data.Sqlite;

namespace QCS
{
    // The model is configured centrally; the database connection is closed before any API call.
    internal static class EmbeddingModelSettings
    {
        internal static string GetModelName()
        {
            using var db = new SqliteConnection(DatabaseManager.GetConnectionString("common"));
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT zx_code_cmmt FROM zx_code_mstr
                WHERE zx_code_fldname='API_EMBEDDING' AND zx_code_value=''
                LIMIT 1";
            string? model = Convert.ToString(cmd.ExecuteScalar())?.Trim();
            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("공통코드 API_EMBEDDING의 zx_code_cmmt에 임베딩 모델을 등록해 주세요.");
            // Current vector persistence and search use the existing 3072-dimension model ID.
            // Prevent mixing incompatible vectors until a model migration is implemented.
            if (!string.Equals(model, "text-embedding-3-large", StringComparison.Ordinal))
                throw new InvalidOperationException($"임베딩 모델 '{model}'은 현재 벡터 저장/검색 구조에서 지원하지 않습니다. 모델 전환 및 재임베딩 기능을 먼저 적용해야 합니다.");
            return model;
        }
    }
}
