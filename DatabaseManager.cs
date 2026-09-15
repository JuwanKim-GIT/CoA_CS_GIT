using System;
using System.IO;
using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using Microsoft.VisualBasic.FileIO;   // TextFieldParser
using ExcelDataReader;

namespace QCS
{
    /// <summary>
    /// 로컬 SQLite DB(QCS_db\qcs.db) 접속·초기화·데이터 임포트를 총괄하는 정적 서비스 클래스.
    /// </summary>
    public static class DatabaseManager
    {
        // ── 상수 ────────────────────────────────────────
        private const string LocalDbFolder = "QCS_db";
        private const string LocalDbFile = "qcs.db";

        // ── 정적 필드 ────────────────────────────────────
        private static string _localDbPath = string.Empty;
        private static string _activeConnectionString = string.Empty;

        // ── 공개 속성 ────────────────────────────────────
        /// <summary>현재 활성 DB 연결문자열. 모든 View는 이 값을 사용하여 DB에 접속한다.</summary>
        public static string ActiveConnectionString => _activeConnectionString;

        /// <summary>현재 활성 DB 파일의 절대 경로</summary>
        public static string ActiveDbPath => _localDbPath;

        /// <summary>로컬 DB 파일 전체 경로 (실행폴더\QCS_db\qcs.db)</summary>
        public static string LocalDbPath => _localDbPath;

        /// <summary>단일 로컬 DB 체제이므로 항상 true 반환</summary>
        public static bool IsOnline => false;

        // ── 이벤트 ────────────────────────────────────────
        /// <summary>DB 접속 상태 변경 이벤트 (기존 UI 호환용)</summary>
        public static event Action<bool>? StatusChanged;

        // ── 공개 메서드 ──────────────────────────────────

        /// <summary>
        /// 주어진 DB 파일 경로에 대해 접속 가능 여부를 반환한다.
        /// </summary>
        public static bool TestConnection(string dbPath)
        {
            if (!File.Exists(dbPath))
                return false;

            try
            {
                string testConnStr = $"Data Source={dbPath};Default Timeout=3;Pooling=False;";
                using (var conn = new SqliteConnection(testConnStr))
                {
                    conn.Open();
                    using (var cmd = new SqliteCommand("SELECT 1;", conn))
                    {
                        cmd.ExecuteScalar();
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 앱 시작 시 1회 호출한다. 실행폴더\QCS_db\qcs.db를 준비하고 연결을 활성화한다.
        /// </summary>
        public static void Initialize()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _localDbPath = Path.Combine(baseDir, LocalDbFolder, LocalDbFile);

            // DB 저장 폴더 생성 보장
            string folder = Path.GetDirectoryName(_localDbPath)!;
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            _activeConnectionString = $"Data Source={_localDbPath};Default Timeout=10;Pooling=True;";

            // 파일 없으면 테이블 생성, 있으면 인덱스 확인
            if (!File.Exists(_localDbPath))
                InitializeTables(_activeConnectionString);
            else
                CreateIndicesForce(_activeConnectionString);

            StatusChanged?.Invoke(false);
        }

        /// <summary>
        /// [기존 UI 호환용] 단일 로컬 DB 체제이므로 재연결 동작 없이 알림만 표시
        /// </summary>
        public static void TryReconnectOnline()
        {
            System.Windows.MessageBox.Show(
                "현재 단일 로컬 DB(QCS_db\\qcs.db) 모드로 동작 중입니다.",
                "DB 상태",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }

        // ── 내부 메서드 ──────────────────────────────────

        /// <summary>
        /// 기본 5개 테이블을 생성하고 인덱스를 빌드한다.
        /// </summary>
        private static void InitializeTables(string connStr)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = new SqliteCommand())
                {
                    cmd.Connection = conn;

                    // [1] pt2_mstr
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS pt2_mstr (
                            pt2_domain TEXT, pt2_part TEXT, pt2_desc1 TEXT, pt2_desc2 TEXT,
                            pt2_um TEXT DEFAULT 'EA', pt2_prod_line TEXT, pt2_part_type TEXT,
                            pt2_status TEXT, pt2_bulk TEXT, pt2_blk_enduse TEXT,
                            pt2_macost REAL DEFAULT 0, pt2_rmcost REAL DEFAULT 0, pt2_rdcost REAL DEFAULT 0,
                            pt2_ohcost REAL DEFAULT 0, pt2_ovcost REAL DEFAULT 0, pt2_sucost REAL DEFAULT 0,
                            pt2_lbcost REAL DEFAULT 0, pt2__dec01 REAL DEFAULT 0, pt2_mfg_part TEXT,
                            pt2_cst_mat REAL DEFAULT 0, pt2_cst_ovh REAL DEFAULT 0, pt2_cst_oth REAL DEFAULT 0,
                            pt2_created TEXT, pt2_updated TEXT, pt2_cst_tot REAL DEFAULT 0,
                            pt2_cst_VMM REAL DEFAULT 0, pt2_sta TEXT, pt2_pr TEXT,
                            pt2_lot_size REAL DEFAULT 0, pt2_alt_um TEXT, pt2_conv REAL DEFAULT 0,
                            pt2_alt_um1 TEXT, pt2_conv1 REAL DEFAULT 0, pt2_shelf INTEGER DEFAULT 0,
                            pt2_char1 TEXT, pt2_char2 TEXT, pt2_char3 TEXT, pt2_char4 TEXT,
                            pt2_deci1 REAL DEFAULT 0, pt2_deci2 REAL DEFAULT 0, pt2_deci3 REAL DEFAULT 0,
                            pt2_deci4 REAL DEFAULT 0, pt2_color_code TEXT,
                            PRIMARY KEY (pt2_domain, pt2_part)
                        );";
                    cmd.ExecuteNonQuery();

                    // [2] zx_code_mstr
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS zx_code_mstr (
                            zx_code_fldname TEXT, zx_code_value TEXT, zx_code_value_up TEXT,
                            zx_code_cmmt TEXT, zx_code_desc1 TEXT, zx_code_desc2 TEXT,
                            zx_code_rmk TEXT, zx_code_char1 TEXT, zx_code_char2 TEXT,
                            zx_code_num1 INTEGER DEFAULT 0, zx_code_num2 INTEGER DEFAULT 0,
                            PRIMARY KEY (zx_code_fldname, zx_code_value)
                        );";
                    cmd.ExecuteNonQuery();

                    // [3] qmir_det
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS qmir_det (
                            qmir_no TEXT, qmir_charac TEXT, qmir_uom TEXT, qmir_batch TEXT,
                            qmir_first_value REAL DEFAULT 0, qmir_last_value REAL DEFAULT 0,
                            qmir_ltol REAL DEFAULT 0, qmir_utol REAL DEFAULT 0,
                            qmir_char1 TEXT, qmir_char2 TEXT, qmir_char3 TEXT, qmir_char4 TEXT,
                            qmir_deci1 REAL DEFAULT 0, qmir_deci2 REAL DEFAULT 0, qmir_deci3 REAL DEFAULT 0,
                            qmir_deci4 REAL DEFAULT 0, qmir_upd_date TEXT, qmir_upd_psn TEXT,
                            qmir_reg_date TEXT, qmir_reg_psn TEXT, qmir_part TEXT, qmir_mf_date TEXT,
                            PRIMARY KEY (qmir_no, qmir_batch)
                        );";
                    cmd.ExecuteNonQuery();

                    // [4] coa_mstr
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS coa_mstr (
                            coa_sdno TEXT, coa_proj_no TEXT, coa_proj_name TEXT, coa_customer TEXT,
                            coa_char1 TEXT, coa_char2 TEXT, coa_char3 TEXT, coa_char4 TEXT,
                            coa_deci1 REAL DEFAULT 0, coa_deci2 REAL DEFAULT 0, coa_deci3 REAL DEFAULT 0,
                            coa_deci4 REAL DEFAULT 0, coa_upd_date TEXT, coa_upd_psn TEXT,
                            coa_reg_date TEXT, coa_reg_psn TEXT, coa_no TEXT PRIMARY KEY
                        );";
                    cmd.ExecuteNonQuery();

                    // [5] coad_det
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS coad_det (
                            coad_sdno TEXT, coad_alt_um1 TEXT, coad_conv1 REAL DEFAULT 0,
                            coad_qty REAL DEFAULT 0, coad_ltqty REAL DEFAULT 0, coad_part TEXT,
                            coad_desc1 TEXT, coad_mf_date TEXT, coad_shelf REAL DEFAULT 0,
                            coad_exp_date TEXT, coad_line INTEGER DEFAULT 0, coad_char1 TEXT,
                            coad_char2 TEXT, coad_char3 TEXT, coad_char4 TEXT,
                            coad_deci1 REAL DEFAULT 0, coad_deci2 REAL DEFAULT 0, coad_deci3 REAL DEFAULT 0,
                            coad_deci4 REAL DEFAULT 0, coad_no TEXT, coad_batch TEXT, coad_color_code TEXT,
                            PRIMARY KEY (coad_no, coad_line)
                        );";
                    cmd.ExecuteNonQuery();
                }
            }

            CreateIndicesForce(connStr);
        }

        /// <summary>
        /// 기본 인덱스를 생성한다.
        /// </summary>
        private static void CreateIndicesForce(string connStr)
        {
            try
            {
                using (var conn = new SqliteConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_pt2_mstr_part ON pt2_mstr (pt2_part);";
                        cmd.ExecuteNonQuery();

                        cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_qmir_det_batch ON qmir_det (qmir_batch);";
                        cmd.ExecuteNonQuery();

                        cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_zx_code_mstr_composite ON zx_code_mstr (zx_code_fldname, zx_code_value);";
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"인덱스 생성 실패: {ex.Message}");
            }
        }

        // ================================================================
        // 📥 Data Import 영역 (Excel / CSV)
        // ================================================================

        public static void ImportExcelToSqlite(string filePath)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            string[] cleanedHeaders;
            var allRows = new List<string[]>();

            using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = ExcelReaderFactory.CreateReader(stream))
            {
                if (!reader.Read())
                    return;

                int colCount = reader.FieldCount;
                cleanedHeaders = new string[colCount];
                for (int col = 0; col < colCount; col++)
                {
                    string rawHeader = reader.GetValue(col)?.ToString() ?? "";
                    cleanedHeaders[col] = rawHeader.Replace("#", "").Replace("\"", "").Trim();
                }

                while (reader.Read())
                {
                    string[] fields = new string[colCount];
                    bool hasData = false;

                    for (int col = 0; col < colCount; col++)
                    {
                        string fieldValue = reader.GetValue(col)?.ToString() ?? "";
                        fields[col] = fieldValue;
                        if (!string.IsNullOrEmpty(fieldValue))
                            hasData = true;
                    }

                    if (hasData)
                        allRows.Add(fields);
                }
            }

            if (allRows.Count == 0)
                return;

            var (tableName, _) = DetectTableFromHeaders(cleanedHeaders);

            using (var conn = new SqliteConnection(ActiveConnectionString))
            {
                conn.Open();
                using (var transaction = conn.BeginTransaction())
                {
                    foreach (var fields in allRows)
                    {
                        ExecuteInsertOrReplace(conn, transaction, tableName, cleanedHeaders, fields);
                    }
                    transaction.Commit();
                }
            }
        }

        public static void ImportCsvToSqlite(string filePath)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            string[] cleanedHeaders;
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, System.Text.Encoding.GetEncoding("euc-kr")))
            {
                if (reader.EndOfStream)
                    return;

                string firstLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(firstLine))
                    return;

                string[] rawHeaders = firstLine.Split(',');
                cleanedHeaders = rawHeaders
                    .Select(h => h.Replace("#", "").Replace("\"", "").Trim())
                    .ToArray();
            }

            var (tableName, _) = DetectTableFromHeaders(cleanedHeaders);

            using (var conn = new SqliteConnection(ActiveConnectionString))
            {
                conn.Open();
                using (var transaction = conn.BeginTransaction())
                {
                    using (var parser = new TextFieldParser(filePath, System.Text.Encoding.GetEncoding("euc-kr")))
                    {
                        parser.TextFieldType = FieldType.Delimited;
                        parser.SetDelimiters(",");
                        parser.HasFieldsEnclosedInQuotes = true;

                        if (!parser.EndOfData)
                            parser.ReadLine();

                        while (!parser.EndOfData)
                        {
                            string[] fields = parser.ReadFields();
                            if (fields == null || fields.Length == 0)
                                continue;

                            ExecuteInsertOrReplace(conn, transaction, tableName, cleanedHeaders, fields);
                        }
                    }
                    transaction.Commit();
                }
            }
        }

        private static (string tableName, string[] requiredKeys) DetectTableFromHeaders(string[] headers)
        {
            var headerSet = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);

            var tableRequiredFields = new Dictionary<string, string[]>
            {
                { "pt2_mstr",     new[] { "pt2_domain", "pt2_part" } },
                { "qmir_det",     new[] { "qmir_batch", "qmir_no" } },
                { "zx_code_mstr", new[] { "zx_code_fldname", "zx_code_value" } }
            };

            foreach (var kvp in tableRequiredFields)
            {
                if (kvp.Value.All(f => headerSet.Contains(f)))
                    return (kvp.Key, kvp.Value);
            }

            string bestGuessTable = "";
            string[] bestGuessRequired = Array.Empty<string>();
            int bestMatchCount = 0;

            foreach (var kvp in tableRequiredFields)
            {
                int matchCount = kvp.Value.Count(f => headerSet.Contains(f));
                if (matchCount > bestMatchCount)
                {
                    bestMatchCount = matchCount;
                    bestGuessTable = kvp.Key;
                    bestGuessRequired = kvp.Value;
                }
            }

            if (bestMatchCount == 0)
            {
                string allPossibleFields = string.Join(", ",
                    tableRequiredFields.Values.SelectMany(v => v).Distinct());

                System.Windows.MessageBox.Show(
                    $"CSV 헤더에서 인식 가능한 필드를 찾을 수 없습니다.\n\n" +
                    $"CSV 헤더: {string.Join(", ", headers)}\n\n" +
                    $"인식 가능한 전체 필수 필드: {allPossibleFields}",
                    "데이터 임포트 오류",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                throw new InvalidOperationException("CSV 헤더 필드 인식 실패");
            }

            var missingFromBest = bestGuessRequired.Where(f => !headerSet.Contains(f)).ToArray();
            string allRequired = string.Join(", ", bestGuessRequired);
            string missingList = string.Join(", ", missingFromBest);

            System.Windows.MessageBox.Show(
                $"CSV 헤더에 필수 필드가 누락되었습니다.\n\n" +
                $"대상 테이블: {bestGuessTable}\n" +
                $"전체 필수 필드: {allRequired}\n" +
                $"누락된 필드: {missingList}",
                "데이터 임포트 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            throw new InvalidOperationException("필수 필드 누락");
        }

        private static void ExecuteInsertOrReplace(
            SqliteConnection conn,
            SqliteTransaction trans,
            string tableName,
            string[] headers,
            string[] fields)
        {
            int length = Math.Min(headers.Length, fields.Length);

            var columns = new List<string>();
            var parameters = new List<string>();

            for (int i = 0; i < length; i++)
            {
                if (string.IsNullOrWhiteSpace(headers[i]))
                    continue;

                columns.Add(headers[i]);
                parameters.Add($"@{headers[i]}");
            }

            StringBuilder sql = new StringBuilder();
            sql.AppendLine($"INSERT OR REPLACE INTO {tableName} ({string.Join(", ", columns)})");
            sql.AppendLine($"VALUES ({string.Join(", ", parameters)});");

            using (var cmd = new SqliteCommand(sql.ToString(), conn, trans))
            {
                for (int i = 0; i < length; i++)
                {
                    if (string.IsNullOrWhiteSpace(headers[i]))
                        continue;

                    cmd.Parameters.AddWithValue($"@{headers[i]}", fields[i] ?? (object)DBNull.Value);
                }
                cmd.ExecuteNonQuery();
            }
        }
    }
}