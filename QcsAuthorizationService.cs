using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace QCS
{
    internal static class QcsAuthorizationService
    {
        internal const int LevelAdmin = 9;
        internal const int LevelSystemOperator = 8;
        internal const int LevelUser = 1;
        internal const int LevelInactive = 0;
        internal const string FgRegistrationProgramId = "MsdsRegistrationView.xaml.cs#FG";
        internal const string RmRegistrationProgramId = "MsdsRegistrationView.xaml.cs#RM";
        internal static readonly (string ParentId, string Id, string Name)[] SubPrograms = Array.Empty<(string, string, string)>();

        internal static bool IsAdmin(int level) => level == LevelAdmin;
        internal static bool IsSystemOperator(int level) => level == LevelSystemOperator;
        internal static bool IsActive(int level) => level == LevelAdmin || level == LevelSystemOperator || level == LevelUser;
        internal static bool CanManageUsers(int level) => level == LevelAdmin || level == LevelSystemOperator;
        internal static bool CanManageProgramPermissions(int level) => level == LevelAdmin || level == LevelSystemOperator;
        internal static bool CanModifyAdminAccount(int level) => level == LevelAdmin;

        internal static string LevelName(int level) => level switch
        {
            LevelAdmin => "IT 관리자",
            LevelSystemOperator => "시스템관리자",
            LevelUser => "일반사용자",
            _ => "비활성"
        };

        private static SqliteConnection OpenCommon()
        {
            var conn = new SqliteConnection(DatabaseManager.GetConnectionString("common"));
            conn.Open();
            return conn;
        }

        internal static void EnsureSecurityMetadata()
        {
            using var conn = OpenCommon();
            using var tx = conn.BeginTransaction();

            var levels = new[]
            {
                ("9", "Admin", "IT 관리자"),
                ("8", "System Operator", "시스템관리자"),
                ("1", "User", "일반사용자"),
                ("0", "Inactive", "비활성")
            };
            foreach (var level in levels)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO zx_code_mstr
(zx_code_fldname,zx_code_value,zx_code_desc1,zx_code_desc2)
SELECT 'QCS_USER_LEVEL',$value,$desc1,$desc2
WHERE NOT EXISTS (SELECT 1 FROM zx_code_mstr WHERE zx_code_fldname='QCS_USER_LEVEL' AND zx_code_value=$value)";
                cmd.Parameters.AddWithValue("$value", level.Item1);
                cmd.Parameters.AddWithValue("$desc1", level.Item2);
                cmd.Parameters.AddWithValue("$desc2", level.Item3);
                cmd.ExecuteNonQuery();
            }

            // Existing application metadata is preserved. Only empty metadata slots are initialized.
            using (var defaults = conn.CreateCommand())
            {
                defaults.Transaction = tx;
                defaults.CommandText = @"UPDATE zx_code_mstr
SET zx_code_char1 = CASE WHEN zx_code_char1 IS NULL OR TRIM(zx_code_char1)='' THEN 'N' ELSE zx_code_char1 END,
    zx_code_char2 = CASE WHEN zx_code_char2 IS NULL OR TRIM(zx_code_char2)='' THEN 'MENU' ELSE zx_code_char2 END
WHERE zx_code_fldname='QCS_APPLICATIONS'";
                defaults.ExecuteNonQuery();
            }

            using (var seed = conn.CreateCommand())
            {
                seed.Transaction = tx;
                seed.CommandText = @"INSERT OR IGNORE INTO zx_code_mstr (zx_code_fldname,zx_code_value,zx_code_desc1,zx_code_desc2,zx_code_cmmt,zx_code_char1,zx_code_char2) VALUES ('QCS_APPLICATIONS','UserCodeListView.xaml.cs','업무코드 관리','시스템 > 업무코드 관리','*','N','MENU')";
                seed.ExecuteNonQuery();
                seed.CommandText = "UPDATE zx_code_mstr SET zx_code_char1='Y' WHERE zx_code_fldname='QCS_APPLICATIONS' AND zx_code_value='CodeListView.xaml.cs'";
                seed.ExecuteNonQuery();
            }
            using (var learning = conn.CreateCommand())
            {
                learning.Transaction = tx;
                learning.CommandText = @"INSERT OR IGNORE INTO zx_code_mstr
(zx_code_fldname,zx_code_value,zx_code_desc1,zx_code_desc2,zx_code_cmmt,zx_code_char1,zx_code_char2)
VALUES ('QCS_APPLICATIONS','MsdsLearningManagementView.xaml.cs','AI 교정학습 관리','MSDS > AI 교정학습 관리','','N','MENU')";
                learning.ExecuteNonQuery();
            }
            // Preserve existing registration access on first initialization of each split menu.
            foreach (var entry in new[] { (FgRegistrationProgramId, "제품 MSDS 등록"), (RmRegistrationProgramId, "원료 MSDS 등록") })
            {
                using var cmd = conn.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = @"INSERT OR IGNORE INTO zx_code_mstr
(zx_code_fldname,zx_code_value,zx_code_desc1,zx_code_desc2,zx_code_cmmt,zx_code_char1,zx_code_char2)
SELECT 'QCS_APPLICATIONS',$id,$name,'MSDS > ' || $name,
COALESCE((SELECT zx_code_cmmt FROM zx_code_mstr WHERE zx_code_fldname='QCS_APPLICATIONS' AND zx_code_value='MsdsRegistrationView.xaml.cs'),''),
COALESCE((SELECT zx_code_char1 FROM zx_code_mstr WHERE zx_code_fldname='QCS_APPLICATIONS' AND zx_code_value='MsdsRegistrationView.xaml.cs'),'N'),'MENU'";
                cmd.Parameters.AddWithValue("$id",entry.Item1); cmd.Parameters.AddWithValue("$name",entry.Item2); cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        internal static bool CanAccessProgram(QcsUser? user, string programId)
        {
            if (user == null || !IsActive(user.UserLevel)) return false;
            if (IsAdmin(user.UserLevel)) return true;

            using var conn = OpenCommon();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT zx_code_cmmt,zx_code_char1 FROM zx_code_mstr
WHERE zx_code_fldname='QCS_APPLICATIONS' AND zx_code_value=$id LIMIT 1";
            cmd.Parameters.AddWithValue("$id", programId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return false;
            string permission = reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString() ?? "";
            string adminOnly = reader.IsDBNull(1) ? "" : reader.GetValue(1).ToString() ?? "";
            if (string.Equals(adminOnly.Trim(), "Y", StringComparison.OrdinalIgnoreCase)) return false;
            // System Operators may access every registered non-Admin-only program.
            if (IsSystemOperator(user.UserLevel)) return true;
            if (permission.Trim() == "*") return true;
            return permission.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(id => string.Equals(id, user.WindowsId, StringComparison.OrdinalIgnoreCase));
        }
    }
}
