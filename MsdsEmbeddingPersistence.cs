using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace QCS
{
    // Offline deterministic character n-gram feature vectors (lexical baseline, NOT a semantic AI model).
    // Stored with an explicit model ID so future semantic vectors cannot be mixed with these vectors.
    internal static class MsdsEmbeddingPersistence
    {
        internal const string ModelId = "AE_CHAR3_256_V1";
        private const int Dimensions = 256;
        private static readonly Regex Digits = new(@"\d+(?:[.,]\d+)*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static byte[] Encode(string input)
        {
            // Numeric placeholders prevent an exact concentration/time from dominating text similarity.
            string text = Digits.Replace(input.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), "#");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            var vector = new float[Dimensions];
            if (text.Length == 0) return new byte[Dimensions * sizeof(float)];
            string padded = "^" + text + "$";
            for (int i = 0; i < padded.Length - 2; i++)
            {
                byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(padded.Substring(i, 3)));
                int index = (int)(BitConverter.ToUInt32(hash, 0) % Dimensions);
                vector[index] += (hash[4] & 1) == 0 ? 1f : -1f;
            }
            double norm = Math.Sqrt(vector.Sum(v => (double)v * v));
            if (norm > 0) for (int i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
            var bytes = new byte[Dimensions * sizeof(float)];
            Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        // Idempotent backfill: existing confirmed corrections are indexed without rewriting their histories.
        internal static void Sync(SqliteConnection db, SqliteTransaction tx)
        {
            using (var model = db.CreateCommand())
            {
                model.Transaction = tx;
                model.CommandText = @"INSERT OR IGNORE INTO ai_embedding_mstr
                    (ai_embedding_id, ai_embedding_model_name, ai_embedding_model_version,
                     ai_embedding_dimensions, ai_embedding_metric, ai_embedding_normalized, ai_embedding_created_at)
                    VALUES ($id,'QCS_CHAR_NGRAM_HASH','1',256,'COSINE',1,$at)";
                model.Parameters.AddWithValue("$id", ModelId);
                model.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
                model.ExecuteNonQuery();
            }
            var pending = new List<(string id, string source, string context)>();
            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"SELECT ai_correctionh_id,
                    COALESCE(NULLIF(ai_correctionh_source_text,''), ai_correctionh_original_value, ''),
                    COALESCE(ai_correctionh_source_context,'')
                    FROM ai_correctionh_hist
                    WHERE ai_correctionh_learning_status='APPROVED'";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) pending.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            foreach (var item in pending)
            {
                SaveText(db, tx, item.id, "SOURCE", item.source);
                SaveText(db, tx, item.id, "CONTEXT", item.context);
            }
        }

        private static void SaveText(SqliteConnection db, SqliteTransaction tx, string correctionId, string type, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT OR IGNORE INTO ai_embeddingd_det
                (ai_embeddingd_embedding_id, ai_embeddingd_correction_id, ai_embeddingd_text_type,
                 ai_embeddingd_text, ai_embeddingd_text_hash, ai_embeddingd_vector, ai_embeddingd_created_at)
                 VALUES ($model,$correction,$type,$text,$hash,$vector,$at)";
            cmd.Parameters.AddWithValue("$model", ModelId);
            cmd.Parameters.AddWithValue("$correction", correctionId);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$text", text);
            cmd.Parameters.AddWithValue("$hash", hash);
            cmd.Parameters.AddWithValue("$vector", Encode(text));
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
    }
}
