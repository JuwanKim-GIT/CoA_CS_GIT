using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace QCS
{
    internal sealed record QcsUser(string WindowsId, string Name, string Department, int UserLevel, string Email = "", string Phone = "");

    internal static class StartupUserService
    {
        internal static Dictionary<string, string> CheckDatabases()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string module in new[] { "msds", "coa", "quality", "reference", "common", "message" })
            {
                try
                {
                    string path = DatabaseManager.GetDbPath(module);
                    if (!System.IO.File.Exists(path)) throw new System.IO.FileNotFoundException("DB 파일 없음", path);
                    using var conn = new SqliteConnection(DatabaseManager.GetConnectionString(module));
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT 1";
                    cmd.ExecuteScalar();
                    result[module] = "정상";
                }
                catch (Exception ex) { result[module] = "오류: " + ex.Message; }
            }
            return result;
        }

        private static SqliteConnection OpenCommon()
        {
            var conn = new SqliteConnection(DatabaseManager.GetConnectionString("common"));
            conn.Open();
            return conn;
        }

        private static void ValidateColumns(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(zx_code_mstr)";
            using var reader = cmd.ExecuteReader();
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read()) columns.Add(reader.GetString(1));
            string[] required = { "zx_code_fldname", "zx_code_value", "zx_code_desc1", "zx_code_desc2", "zx_code_cmmt", "zx_code_char1", "zx_code_char2" };
            string[] missing = required.Where(c => !columns.Contains(c)).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException("zx_code_mstr 테이블의 필수 컬럼을 확인할 수 없습니다: " + string.Join(", ", missing));
        }

        internal static int ParseLevel(string? value) => (value ?? "").Trim() switch
        {
            "9" => 9,
            "1" => 1,
            _ => 0 // Legacy O/X or unexpected values require explicit DB migration.
        };

        internal static QcsUser? FindCurrentUser()
        {
            using var conn = OpenCommon();
            ValidateColumns(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT zx_code_desc1, zx_code_desc2, zx_code_cmmt, zx_code_char1, zx_code_char2 FROM zx_code_mstr WHERE zx_code_fldname = 'QCS_USERS' AND zx_code_value = $id LIMIT 1";
            cmd.Parameters.AddWithValue("$id", Environment.UserName);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new QcsUser(Environment.UserName, reader.IsDBNull(0) ? "" : reader.GetString(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                ParseLevel(reader.IsDBNull(2) ? "" : reader.GetValue(2).ToString()),
                reader.IsDBNull(3) ? "" : reader.GetString(3), reader.IsDBNull(4) ? "" : reader.GetString(4));
        }

        internal static QcsUser Register(string name, string department, string email = "", string phone = "")
        {
            name = (name ?? "").Trim(); department = (department ?? "").Trim();
            email = (email ?? "").Trim(); phone = (phone ?? "").Trim();
            if (name.Length == 0 || department.Length == 0) throw new ArgumentException("이름과 부서를 모두 입력해 주세요.");
            using var conn = OpenCommon();
            ValidateColumns(conn);
            using var transaction = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "INSERT INTO zx_code_mstr (zx_code_fldname, zx_code_value, zx_code_desc1, zx_code_desc2, zx_code_cmmt, zx_code_char1, zx_code_char2) " +
                              "SELECT 'QCS_USERS', $id, $name, $dept, '0', $email, $phone WHERE NOT EXISTS " +
                              "(SELECT 1 FROM zx_code_mstr WHERE zx_code_fldname = 'QCS_USERS' AND zx_code_value = $id)";
            cmd.Parameters.AddWithValue("$id", Environment.UserName);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$dept", department);
            cmd.Parameters.AddWithValue("$email", email);
            cmd.Parameters.AddWithValue("$phone", phone);
            int inserted = cmd.ExecuteNonQuery();
            transaction.Commit();
            if (inserted != 1) throw new InvalidOperationException("이미 등록된 사용자입니다. 다시 실행하여 사용자 정보를 조회해 주세요.");
            return new QcsUser(Environment.UserName, name, department, 0, email, phone);
        }
    }
}
