using System;
using System.IO;
using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.VisualBasic.FileIO;   // TextFieldParser
using ExcelDataReader;

namespace QCS
{
    /// <summary>
    /// QCS SQLite DB 연결 및 데이터 Import를 관리하는 정적 서비스 클래스.
    /// </summary>
    public static class DatabaseManager
    {
        // 설정 파일은 실제 QCS.exe가 있는 폴더에서 읽는다.
        private sealed class DatabaseSettings
        {
            public string NetworkDbFolder { get; set; } = "";
            public Dictionary<string, string> Databases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public ConnectionSettings Connection { get; set; } = new();
        }

        private sealed class ConnectionSettings
        {
            public int DefaultTimeout { get; set; } = 10;
            public bool Pooling { get; set; } = true;
        }

        private static readonly string[] RequiredModules =
            { "msds", "coa", "quality", "reference", "common", "message" };

        private static readonly Lazy<DatabaseSettings> Settings = new(LoadSettings);
        private static DatabaseSettings Config => Settings.Value;
        private static Dictionary<string, string> DbFiles => Config.Databases;

        private static DatabaseSettings LoadSettings()
        {
            string exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("실행 파일 경로를 확인할 수 없습니다.");
            string configPath = Path.Combine(Path.GetDirectoryName(exePath)!, "Database.json");
            if (!File.Exists(configPath))
                throw new FileNotFoundException($"DB 설정 파일이 없습니다: {configPath}", configPath);

            DatabaseSettings settings;
            try
            {
                settings = JsonSerializer.Deserialize<DatabaseSettings>(File.ReadAllText(configPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("설정 내용이 비어 있습니다.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Database.json 형식 오류: {ex.Message}", ex);
            }

            if (string.IsNullOrWhiteSpace(settings.NetworkDbFolder))
                throw new InvalidDataException("Database.json: NetworkDbFolder가 비어 있습니다.");
            settings.Databases = new Dictionary<string, string>(settings.Databases ?? new(),
                StringComparer.OrdinalIgnoreCase);
            foreach (string module in RequiredModules)
            {
                if (!settings.Databases.TryGetValue(module, out string? file) ||
                    string.IsNullOrWhiteSpace(file) || file != Path.GetFileName(file))
                    throw new InvalidDataException($"Database.json: {module} DB 파일명이 올바르지 않습니다.");
            }
            if (settings.Connection is null || settings.Connection.DefaultTimeout < 0)
                throw new InvalidDataException("Database.json: Connection 설정이 올바르지 않습니다.");
            return settings;
        }

        public static string GetDbPath(string module) =>
            Path.Combine(Config.NetworkDbFolder, DbFiles[module]);

        public static string GetConnectionString(string module) =>
            new SqliteConnectionStringBuilder
            {
                DataSource = GetDbPath(module),
                Mode = SqliteOpenMode.ReadWrite,
                DefaultTimeout = Config.Connection.DefaultTimeout,
                Pooling = Config.Connection.Pooling
            }.ToString();

        /// <summary>모듈 DB를 기본 DB로 열고 나머지 DB를 ATTACH한다. 호출자가 using으로 닫아야 한다.</summary>
        public static SqliteConnection OpenConnection(string module)
        {
            if (!DbFiles.ContainsKey(module)) throw new ArgumentException($"알 수 없는 DB 모듈: {module}");
            var conn = new SqliteConnection(GetConnectionString(module));
            try
            {
                conn.Open();
                // 연결 풀에서 재사용된 SQLite 연결에 기존 ATTACH가 남아 있을 수 있다.
                // 이미 연결된 별칭은 건너뛰되, 실제 DB 파일 경로가 다른 경우 오류로 처리한다.
                var attached = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using (var listCmd = conn.CreateCommand())
                {
                    listCmd.CommandText = "PRAGMA database_list;";
                    using var reader = listCmd.ExecuteReader();
                    while (reader.Read())
                        attached[reader.GetString(1)] = reader.IsDBNull(2) ? "" : reader.GetString(2);
                }
                foreach (var item in DbFiles)
                {
                    if (item.Key.Equals(module, StringComparison.OrdinalIgnoreCase)) continue;
                    string expectedPath = Path.GetFullPath(GetDbPath(item.Key));
                    if (attached.TryGetValue(item.Key, out string? existingPath))
                    {
                        if (!string.Equals(Path.GetFullPath(existingPath), expectedPath,
                            StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(
                                $"DB ATTACH 별칭 '{item.Key}'이(가) 다른 파일을 가리킵니다: {existingPath}");
                        continue;
                    }
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"ATTACH DATABASE $path AS \"{item.Key}\";";
                    cmd.Parameters.AddWithValue("$path", expectedPath);
                    cmd.ExecuteNonQuery();
                }
                return conn;
            }
            catch { conn.Dispose(); throw; }
        }

        private static string ModuleForTable(string tableName) => tableName switch
        {
            "pt2_mstr" or "cas_ref_mstr" => "reference",
            "qmir_det" => "quality", "zx_code_mstr" => "common", "msg_mstr" => "message",
            "coa_mstr" or "coad_det" => "coa", _ => "msds"
        };

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
        /// 앱 시작 시 1회 호출한다. Database.json을 읽고 공유폴더 DB 연결을 활성화한다.
        /// </summary>
        public static void Initialize()
        {
            foreach (var item in DbFiles)
            {
                if (!File.Exists(GetDbPath(item.Key)))
                    throw new FileNotFoundException($"SQLite DB 파일을 찾을 수 없습니다: {GetDbPath(item.Key)}");
            }
            // 구형 UI의 ActiveConnectionString 호환용. 이 문자열만으로는 ATTACH가 수행되지 않는다.
            _localDbPath = GetDbPath("coa");
            _activeConnectionString = GetConnectionString("coa");
            using (var conn = OpenConnection("coa"))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT 1;";
                cmd.ExecuteScalar();
            }
            CreateIndicesForce();
            StatusChanged?.Invoke(true);
        }

        /// <summary>
        /// [기존 UI 호환용] 단일 로컬 DB 체제이므로 재연결 동작 없이 알림만 표시
        /// </summary>
        public static void TryReconnectOnline()
        {
            System.Windows.MessageBox.Show(
                "현재 네트워크 공유폴더의 6개 SQLite DB를 사용합니다.",
                "DB 상태",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }

        // ── 내부 메서드 ──────────────────────────────────
        /// <summary>
        /// 기본 인덱스를 생성한다.
        /// </summary>
        private static void CreateIndicesForce()
        {
            var indexes = new (string module, string sql)[]
            {
                ("reference", "CREATE INDEX IF NOT EXISTS idx_pt2_mstr_part ON pt2_mstr (pt2_part);"),
                ("quality", "CREATE INDEX IF NOT EXISTS idx_qmir_det_batch ON qmir_det (qmir_batch);"),
                ("common", "CREATE INDEX IF NOT EXISTS idx_zx_code_mstr_composite ON zx_code_mstr (zx_code_fldname, zx_code_value);")
            };
            foreach (var (module, sql) in indexes)
            {
                try
                {
                    using var conn = new SqliteConnection(GetConnectionString(module));
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"{module} 인덱스 생성 실패: {ex.Message}");
                }
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

            using (var conn = new SqliteConnection(GetConnectionString(ModuleForTable(tableName))))
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

            using (var conn = new SqliteConnection(GetConnectionString(ModuleForTable(tableName))))
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
