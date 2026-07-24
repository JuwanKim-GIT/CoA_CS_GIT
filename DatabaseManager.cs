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


namespace CoA_CS
{
    /// <summary>
    /// DB 접속 상태·경로·초기화·동기화를 총괄하는 정적 서비스 클래스.
    /// 앱 시작 시 Initialize()를 호출하여 네트워크/로컬 DB 중 하나를 활성화한다.
    /// 모든 View는 App.ConnectionString 대신 <see cref="ActiveConnectionString"/>을 사용한다.
    /// </summary>
    public static class DatabaseManager
    {
        // ── 상수 ────────────────────────────────────────

        /// <summary>부트스트랩용 기본 로컬 DB 폴더명</summary>
        private const string DefaultLocalDbFolder = "CoA_db";
        /// <summary>부트스트랩용 기본 로컬 DB 파일명</summary>
        private const string DefaultLocalDbFile = "coa_cs.db";

        // ── JSON 설정 모델 ────────────────────────────────

        /// <summary>dbconfig.json 파일의 루트 모델</summary>
        private class DbConfig
        {
            public DatabaseSettings? Database { get; set; }
        }

        /// <summary>DB 경로 설정</summary>
        private class DatabaseSettings
        {
            public string OnlinePath { get; set; } = string.Empty;
            public string OfflinePath { get; set; } = string.Empty;
        }

        // ── 정적 필드 ────────────────────────────────────

        /// <summary>dbconfig.json에서 조회한 Online DB 경로</summary>
        private static string _onlineDbPath = string.Empty;
        /// <summary>dbconfig.json에서 조회한 Offline DB 경로</summary>
        private static string _offlineDbPath = string.Empty;

        private static string _localDbPath = string.Empty;
        private static string _activeConnectionString = string.Empty;
        private static bool _isOnline = false;

        // ── 공개 속성 ────────────────────────────────────

        /// <summary>현재 활성 DB 연결문자열. 모든 View는 이 값을 사용하여 DB에 접속한다.</summary>
        public static string ActiveConnectionString => _activeConnectionString;

        /// <summary>현재 활성 DB 파일의 절대 경로</summary>
        public static string ActiveDbPath => _isOnline ? _onlineDbPath : _offlineDbPath;

        /// <summary>로컬 DB 파일 전체 경로 (실행폴더\CoA_db\coa_cs.db)</summary>
        public static string LocalDbPath => _localDbPath;

        /// <summary>현재 온라인(네트워크 DB) 모드 여부</summary>
        public static bool IsOnline => _isOnline;

        // ── 이벤트 ────────────────────────────────────────

        /// <summary>DB 접속 상태 변경 시 발생. MainWindow에서 UI 갱신용으로 구독한다.</summary>
        public static event Action<bool>? StatusChanged;

        // ── 공개 메서드 ──────────────────────────────────

        /// <summary>
        /// 주어진 DB 파일 경로에 대해 실제 SqliteConnection.Open()을 시도하여 접속 가능 여부를 반환한다.
        /// 짧은 타임아웃(3초)으로 빠르게 판별한다.
        /// </summary>
        /// <param name="dbPath">확인할 .db 파일의 전체 경로</param>
        /// <returns>연결 성공 시 true, 실패 시 false</returns>
        public static bool TestConnection(string dbPath)
        {
            // DB 파일 자체가 존재하지 않으면 빠르게 false 반환
            if (!File.Exists(dbPath))
                return false;

            try
            {
                string testConnStr = $"Data Source={dbPath};Default Timeout=3;Pooling=False;";
                using (var conn = new SqliteConnection(testConnStr))
                {
                    conn.Open();
                    // 간단한 읽기 쿼리로 실제 접속 가능 여부 확인 (테이블 유무 무관)
                    using (var cmd = new SqliteCommand("SELECT 1;", conn))
                    {
                        cmd.ExecuteScalar();
                    }
                }
                return true;
            }
            catch
            {
                // 네트워크 불가, 권한 없음, 파일 잠김 등 모든 예외 → 접속 불가로 판정
                return false;
            }
        }

        /// <summary>
        /// 앱 시작 시 1회 호출한다. dbconfig.json에서 DB 경로를 읽고,
        /// 네트워크 DB 접속을 시도하며, 실패 시 로컬 DB로 fallback한다.
        /// </summary>
        public static void Initialize()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // 1. dbconfig.json에서 DB 경로 로드 (없으면 기본값으로 자동 생성)
            var (onlinePath, offlinePath) = LoadOrCreateDbConfig();
            _onlineDbPath = onlinePath;

            // 2. Offline DB 경로 결정 (상대경로 → 절대경로)
            if (string.IsNullOrEmpty(offlinePath))
            {
                _offlineDbPath = Path.Combine(baseDir, DefaultLocalDbFolder, DefaultLocalDbFile);
            }
            else if (Path.IsPathRooted(offlinePath))
            {
                _offlineDbPath = offlinePath;
            }
            else
            {
                _offlineDbPath = Path.Combine(baseDir, offlinePath);
            }
            _localDbPath = _offlineDbPath;

            // 3. Offline DB 폴더 및 테이블 보장
            string offlineFolder = Path.GetDirectoryName(_offlineDbPath);
            if (!string.IsNullOrEmpty(offlineFolder) && !Directory.Exists(offlineFolder))
                Directory.CreateDirectory(offlineFolder);

            string offlineConnStr = $"Data Source={_offlineDbPath};Default Timeout=10;Pooling=False;";
            if (!File.Exists(_offlineDbPath))
                InitializeTables(offlineConnStr);
            else
                CreateIndicesForce(offlineConnStr);

            // 4. Online DB 연결 시도
            if (!string.IsNullOrEmpty(_onlineDbPath))
            {
                string onlineFolder = Path.GetDirectoryName(_onlineDbPath);
                if (!string.IsNullOrEmpty(onlineFolder) && !Directory.Exists(onlineFolder))
                {
                    try { Directory.CreateDirectory(onlineFolder); }
                    catch { }
                }

                if (TestConnection(_onlineDbPath))
                {
                    // Online DB 테이블 보장
                    string onlineConnStr = $"Data Source={_onlineDbPath};Default Timeout=10;Pooling=True;";
                    if (!File.Exists(_onlineDbPath))
                        InitializeTables(onlineConnStr);
                    else
                        CreateIndicesForce(onlineConnStr);

                    _activeConnectionString = onlineConnStr;
                    _isOnline = true;
                    StatusChanged?.Invoke(_isOnline);
                    return;
                }
            }

            // 5. Offline 모드로 실행
            _activeConnectionString = $"Data Source={_offlineDbPath};Default Timeout=10;Pooling=True;";
            _isOnline = false;
            StatusChanged?.Invoke(_isOnline);
        }

        /// <summary>
        /// [DB 체크] 버튼 클릭 시 호출된다. 네트워크 DB 재연결을 시도하고,
        /// 성공하면 로컬의 미동기화 CoA 레코드를 네트워크 DB로 복사한 후 온라인 모드로 전환한다.
        /// </summary>
        public static void TryReconnectOnline()
        {
            // 이미 온라인이면 아무것도 하지 않음
            if (_isOnline)
                return;

            // dbconfig.json에서 최신 Online 경로 재조회 (관리자가 파일을 수정했을 수 있음)
            var (onlinePath, _) = LoadOrCreateDbConfig();
            if (!string.IsNullOrEmpty(onlinePath))
                _onlineDbPath = onlinePath;

            // 1. 네트워크 DB 접속 가능 여부 확인
            if (string.IsNullOrEmpty(_onlineDbPath) || !TestConnection(_onlineDbPath))
            {
                System.Windows.MessageBox.Show(
                    "네트워크 DB 경로가 설정되지 않았거나 연결할 수 없습니다.\n실행 폴더의 dbconfig.json 파일에서 Database.OnlinePath를 확인해 주세요.",
                    "연결 실패",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            // 2. 네트워크 DB에 테이블/인덱스 보장 (없으면 생성)
            string networkConnStr = $"Data Source={_onlineDbPath};Default Timeout=10;Pooling=True;";
            if (!File.Exists(_onlineDbPath))
            {
                InitializeTables(networkConnStr);
            }
            else
            {
                CreateIndicesForce(networkConnStr);
            }

            // 3. 로컬 → 네트워크 동기화 실행
            SyncToNetwork(networkConnStr);

            // 4. 온라인 모드로 전환
            _activeConnectionString = networkConnStr;
            _isOnline = true;

            // 5. 상태 변경 알림
            StatusChanged?.Invoke(_isOnline);

            System.Windows.MessageBox.Show(
                "네트워크 DB 연결이 복구되었습니다.\n로컬에서 발행된 CoA 데이터가 동기화되었습니다.",
                "연결 성공",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }

        // ── 내부 메서드 ──────────────────────────────────

        /// <summary>
        /// 실행 폴더의 dbconfig.json 파일에서 DB 경로를 읽는다.
        /// 파일이 없거나 파싱에 실패하면 기본값으로 새 파일을 생성한다.
        /// </summary>
        /// <returns>(onlinePath, offlinePath) 튜플</returns>
        private static (string onlinePath, string offlinePath) LoadOrCreateDbConfig()
        {
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dbconfig.json");

            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    var config = JsonSerializer.Deserialize<DbConfig>(json);
                    if (config?.Database != null)
                    {
                        return (config.Database.OnlinePath ?? string.Empty,
                                config.Database.OfflinePath ?? string.Empty);
                    }
                }
                catch { /* 파싱 실패 시 기본값으로 새로 생성 */ }
            }

            // JSON 파일이 없거나 파싱 실패 → 기본값으로 새로 생성
            var defaultConfig = new DbConfig
            {
                Database = new DatabaseSettings
                {
                    OnlinePath = string.Empty,
                    OfflinePath = ".\\CoA_db\\coa_cs.db"
                }
            };

            try
            {
                string defaultJson = JsonSerializer.Serialize(defaultConfig,
                    new JsonSerializerOptions { WriteIndented = true });
                string configDir = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(configDir) && !Directory.Exists(configDir))
                    Directory.CreateDirectory(configDir);
                File.WriteAllText(configPath, defaultJson, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"dbconfig.json 생성 실패: {ex.Message}");
            }

            return (string.Empty, ".\\CoA_db\\coa_cs.db");
        }

        /// <summary>
        /// 로컬 DB의 coa_mstr, coad_det 테이블에서 coa_char4/coad_char4 = '1' 인
        /// 미동기화 레코드를 네트워크 DB로 복사한 후, 로컬의 flag를 ''(공백)으로 갱신한다.
        /// </summary>
        /// <param name="networkConnStr">네트워크 DB 연결문자열</param>
        private static void SyncToNetwork(string networkConnStr)
        {
            // 로컬에 미동기화 레코드가 있는지 먼저 확인
            int pendingCount = 0;
            try
            {
                using (var localConn = new SqliteConnection($"Data Source={_localDbPath};Default Timeout=10;Pooling=False;"))
                {
                    localConn.Open();
                    using (var cmd = new SqliteCommand(
                        "SELECT COUNT(*) FROM coa_mstr WHERE coa_char4 = '1';", localConn))
                    {
                        pendingCount = Convert.ToInt32(cmd.ExecuteScalar());
                    }
                }
            }
            catch
            {
                // 로컬 DB에 coa_mstr 테이블이 아직 없거나 읽기 실패 → 동기화 건너뜀
                return;
            }

            if (pendingCount == 0)
                return; // 동기화할 레코드 없음

            try
            {
                using (var networkConn = new SqliteConnection(networkConnStr))
                {
                    networkConn.Open();

                    // ATTACH DATABASE로 로컬 DB를 'localdb'라는 별칭으로 연결
                    string attachSql = $"ATTACH DATABASE '{_localDbPath.Replace("'", "''")}' AS localdb;";
                    using (var attachCmd = new SqliteCommand(attachSql, networkConn))
                    {
                        attachCmd.ExecuteNonQuery();
                    }

                    try
                    {
                        using (var transaction = networkConn.BeginTransaction())
                        {
                            // 1) coa_mstr 동기화 (coa_char4='1' 인 행 → 네트워크로 INSERT OR IGNORE)
                            //    네트워크로 복사할 때는 coa_char4를 ''로 비워서 저장 (동기화 완료 상태)
                            string syncMstrSql = @"
                                INSERT OR IGNORE INTO coa_mstr (
                                    coa_sdno, coa_proj_no, coa_proj_name, coa_customer,
                                    coa_char1, coa_char2, coa_char3, coa_char4,
                                    coa_deci1, coa_deci2, coa_deci3, coa_deci4,
                                    coa_upd_date, coa_upd_psn, coa_reg_date, coa_reg_psn,
                                    coa_no
                                )
                                SELECT 
                                    coa_sdno, coa_proj_no, coa_proj_name, coa_customer,
                                    coa_char1, coa_char2, coa_char3, '',
                                    coa_deci1, coa_deci2, coa_deci3, coa_deci4,
                                    coa_upd_date, coa_upd_psn, coa_reg_date, coa_reg_psn,
                                    coa_no
                                FROM localdb.coa_mstr WHERE coa_char4 = '1';";

                            using (var cmd = new SqliteCommand(syncMstrSql, networkConn, transaction))
                            {
                                cmd.ExecuteNonQuery();
                            }

                            // 2) coad_det 동기화 (coad_char4='1' 인 행 → 네트워크로 INSERT OR IGNORE)
                            string syncDetSql = @"
                                INSERT OR IGNORE INTO coad_det (
                                    coad_sdno, coad_alt_um1, coad_conv1,
                                    coad_qty, coad_ltqty, coad_part,
                                    coad_desc1, coad_mf_date, coad_shelf,
                                    coad_exp_date, coad_line, coad_char1,
                                    coad_char2, coad_char3, coad_char4,
                                    coad_deci1, coad_deci2, coad_deci3, coad_deci4,
                                    coad_no, coad_batch, coad_color_code
                                )
                                SELECT
                                    coad_sdno, coad_alt_um1, coad_conv1,
                                    coad_qty, coad_ltqty, coad_part,
                                    coad_desc1, coad_mf_date, coad_shelf,
                                    coad_exp_date, coad_line, coad_char1,
                                    coad_char2, coad_char3, '',
                                    coad_deci1, coad_deci2, coad_deci3, coad_deci4,
                                    coad_no, coad_batch, coad_color_code
                                FROM localdb.coad_det WHERE coad_char4 = '1';";

                            using (var cmd = new SqliteCommand(syncDetSql, networkConn, transaction))
                            {
                                cmd.ExecuteNonQuery();
                            }

                            // 3) 로컬 DB의 sync_flag 갱신 (coa_char4, coad_char4 → '')
                            string updateMstrSql = "UPDATE localdb.coa_mstr SET coa_char4 = '' WHERE coa_char4 = '1';";
                            using (var cmd = new SqliteCommand(updateMstrSql, networkConn, transaction))
                            {
                                cmd.ExecuteNonQuery();
                            }

                            string updateDetSql = "UPDATE localdb.coad_det SET coad_char4 = '' WHERE coad_char4 = '1';";
                            using (var cmd = new SqliteCommand(updateDetSql, networkConn, transaction))
                            {
                                cmd.ExecuteNonQuery();
                            }

                            transaction.Commit();
                        }
                    }
                    finally
                    {
                        // DETACH DATABASE
                        try
                        {
                            using (var detachCmd = new SqliteCommand("DETACH DATABASE localdb;", networkConn))
                            {
                                detachCmd.ExecuteNonQuery();
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"동기화 실패: {ex.Message}");
                throw; // TryReconnectOnline에서 처리
            }
        }

        /// <summary>
        /// 주어진 연결문자열의 DB에 5개 테이블(pt2_mstr, zx_code_mstr, qmir_det, coa_mstr, coad_det)을
        /// 생성하고 인덱스를 빌드한다. 기존 App.xaml.cs의 _initializeTables() 로직을 이관.
        /// </summary>
        /// <param name="connStr">테이블을 생성할 DB의 연결문자열</param>
        private static void InitializeTables(string connStr)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = new SqliteCommand())
                {
                    cmd.Connection = conn;

                    // [1] pt2_mstr (제품 정보 마스터)
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

                    // [2] zx_code_mstr (공통 코드 마스터)
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS zx_code_mstr (
                            zx_code_fldname TEXT, zx_code_value TEXT, zx_code_value_up TEXT,
                            zx_code_cmmt TEXT, zx_code_desc1 TEXT, zx_code_desc2 TEXT,
                            zx_code_rmk TEXT, zx_code_char1 TEXT, zx_code_char2 TEXT,
                            zx_code_num1 INTEGER DEFAULT 0, zx_code_num2 INTEGER DEFAULT 0,
                            PRIMARY KEY (zx_code_fldname, zx_code_value)
                        );";
                    cmd.ExecuteNonQuery();

                    // [3] qmir_det (품목별 검사 항목 마스터)
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

                    // [4] coa_mstr (CoA 마스터 헤더)
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS coa_mstr (
                            coa_sdno TEXT, coa_proj_no TEXT, coa_proj_name TEXT, coa_customer TEXT,
                            coa_char1 TEXT, coa_char2 TEXT, coa_char3 TEXT, coa_char4 TEXT,
                            coa_deci1 REAL DEFAULT 0, coa_deci2 REAL DEFAULT 0, coa_deci3 REAL DEFAULT 0,
                            coa_deci4 REAL DEFAULT 0, coa_upd_date TEXT, coa_upd_psn TEXT,
                            coa_reg_date TEXT, coa_reg_psn TEXT, coa_no TEXT PRIMARY KEY
                        );";
                    cmd.ExecuteNonQuery();

                    // [5] coad_det (CoA 상세 결과 디테일)
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

            // 테이블 생성 후 인덱스도 함께 빌드
            CreateIndicesForce(connStr);
        }

        /// <summary>
        /// 기존 DB에 커스텀 인덱스를 강제로 생성한다 (IF NOT EXISTS).
        /// 기존 App.xaml.cs의 _createIndicesForce() 로직을 이관.
        /// </summary>
        /// <param name="connStr">인덱스를 생성할 DB의 연결문자열</param>
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
                System.Diagnostics.Debug.WriteLine($"인덱스 주입 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// Excel 파일(.xlsx/.xls)의 첫 번째 시트를 읽어 헤더 기반으로 대상 테이블을 자동 판별하고,
        /// INSERT OR REPLACE 방식으로 Upsert를 수행한다.
        /// </summary>
        /// <param name="filePath">임포트할 Excel 파일의 전체 경로</param>
        /// <exception cref="InvalidOperationException">필수 필드 누락 시 발생</exception>
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

            var (tableName, requiredKeys) = DetectTableFromHeaders(cleanedHeaders);

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

         // ================================================================
         // 📥 Data Import 영역 (헤더 기반 칼럼 매핑 + Upsert)
         // ================================================================

         /// <summary>
         /// CSV 파일을 읽어 헤더 기반으로 대상 테이블을 자동 판별하고,
         /// INSERT OR REPLACE 방식으로 Upsert를 수행한다.
         /// </summary>
         /// <param name="filePath">임포트할 CSV 파일의 전체 경로</param>
         /// <exception cref="InvalidOperationException">필수 필드 누락 시 발생</exception>
         public static void ImportCsvToSqlite(string filePath)
         {
             System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

             // ── 첫 줄(헤더) 파싱 ──
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

             // ── 헤더 기반 테이블 판별 (Index 0 고정 검사 제거) ──
             var (tableName, requiredKeys) = DetectTableFromHeaders(cleanedHeaders);

             // ── 데이터 행 Upsert ──
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

                         // 첫 줄(헤더) 건너뛰기
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

         /// <summary>
         /// 헤더 배열에서 각 테이블별 필수 필드 세트를 검사하여 대상 테이블을 판별한다.
         /// 필수 필드가 하나라도 누락되면 상세 메시지 박스를 표시하고 예외를 발생시킨다.
         /// </summary>
         /// <param name="headers">CSV 첫 줄에서 추출한 헤더 문자열 배열</param>
         /// <returns>판별된 테이블명과 필수 키 배열</returns>
         /// <exception cref="InvalidOperationException">어떤 테이블의 필수 필드도 완전히 충족되지 않은 경우</exception>
         private static (string tableName, string[] requiredKeys) DetectTableFromHeaders(string[] headers)
         {
             var headerSet = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);

             var tableRequiredFields = new Dictionary<string, string[]>
             {
                 { "pt2_mstr",     new[] { "pt2_domain", "pt2_part" } },
                 { "qmir_det",     new[] { "qmir_batch", "qmir_no" } },
                 { "zx_code_mstr", new[] { "zx_code_fldname", "zx_code_value" } }
             };

             // 1차: 모든 필수 필드 만족하는 테이블 찾기
             foreach (var kvp in tableRequiredFields)
             {
                 if (kvp.Value.All(f => headerSet.Contains(f)))
                     return (kvp.Key, kvp.Value);
             }

             // 2차: 가장 많이 매칭된 테이블 기준 누락 정보 생성
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
                     $"인식 가능한 전체 필수 필드: {allPossibleFields}\n\n" +
                     $"CSV 파일 형식을 확인해 주세요.",
                     "데이터 임포트 오류",
                     MessageBoxButton.OK,
                     MessageBoxImage.Error);

                 throw new InvalidOperationException(
                     $"CSV 헤더에서 인식 가능한 필드 없음. 헤더: {string.Join(", ", headers)}");
             }

             var missingFromBest = bestGuessRequired.Where(f => !headerSet.Contains(f)).ToArray();
             string allRequired = string.Join(", ", bestGuessRequired);
             string missingList = string.Join(", ", missingFromBest);

             System.Windows.MessageBox.Show(
                 $"CSV 헤더에 필수 필드가 누락되었습니다.\n\n" +
                 $"대상 테이블 (추정): {bestGuessTable}\n" +
                 $"전체 필수 필드: {allRequired}\n" +
                 $"누락된 필드: {missingList}\n\n" +
                 $"CSV 파일을 확인해 주세요.",
                 "데이터 임포트 오류",
                 MessageBoxButton.OK,
                 MessageBoxImage.Error);

             throw new InvalidOperationException(
                 $"필수 필드 누락 - 테이블: {bestGuessTable}, 전체 필수: [{allRequired}], 누락: [{missingList}]");
         }

         /// <summary>
         /// INSERT OR REPLACE 방식으로 Upsert를 실행한다.
         /// 필수 키 조합(PK)이 이미 존재하면 해당 행을 덮어쓰고, 없으면 새로 삽입한다.
         /// 헤더 이름 기준으로 칼럼을 매핑하므로 CSV 열 순서는 무관하다.
         /// </summary>
         /// <param name="conn">활성 SQLite 연결</param>
         /// <param name="trans">활성 트랜잭션</param>
         /// <param name="tableName">대상 테이블명</param>
         /// <param name="headers">CSV 헤더 배열</param>
         /// <param name="fields">현재 데이터 행의 필드 값 배열</param>
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
