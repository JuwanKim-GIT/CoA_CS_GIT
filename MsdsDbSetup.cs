using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace QCS
{
    public static class MsdsDbSetup
    {
        private const string DbPath =
            @"\\zcnpo0317vn0001\IPK_DBs\SQL_DBs\QCS\qcs.db";

        private static readonly string[] Tables =
        {
            "msds_mstr",
            "msds_comp_dtl",
            "msds_oper_dtl",
            "cas_ref_mstr",
            "reg_result_dtl",
            "extract_result_dtl",
            "audit_log_hist"
        };

        public static string Run(string sqlFilePath)
        {
            // 기존 DB 파일이 없으면 실행 중단
            if (!File.Exists(DbPath))
                throw new FileNotFoundException(
                    "네트워크 QCS DB 파일이 없습니다.", DbPath);

            // SQL 파일이 없으면 실행 중단
            if (!File.Exists(sqlFilePath))
                throw new FileNotFoundException(
                    "MSDS 테이블 생성 SQL 파일을 찾을 수 없습니다.",
                    sqlFilePath);

            string script = File.ReadAllText(sqlFilePath);

            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = DbPath,
                Mode = SqliteOpenMode.ReadWrite,
                DefaultTimeout = 30,
                Pooling = false
            };

            using var conn = new SqliteConnection(csb.ToString());
            conn.Open();

            // SQL 실행
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = script;
                cmd.CommandTimeout = 30;
                cmd.ExecuteNonQuery();
            }

            // 생성된 테이블 확인
            using var verify = conn.CreateCommand();
            verify.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table';";

            var actual = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            using (var reader = verify.ExecuteReader())
            {
                while (reader.Read())
                    actual.Add(reader.GetString(0));
            }

            var missing = Tables
                .Where(t => !actual.Contains(t))
                .ToArray();

            if (missing.Length > 0)
                throw new InvalidOperationException(
                    "테이블 생성 검증 실패: " +
                    string.Join(", ", missing));

            return "MSDS 테이블 7개 확인 완료: " +
                   string.Join(", ", Tables);
        }
    }
}