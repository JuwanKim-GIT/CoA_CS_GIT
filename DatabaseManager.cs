using System;
using System.IO;
using Microsoft.Data.Sqlite;

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

        /// <summary>네트워크 공유 DB 파일 전체 경로</summary>
        public const string NetworkDbPath = @"\\zcnpo0317vn0001\IPK_DBs\db_files\coa_cs.db";

        // ── 정적 필드 ────────────────────────────────────

        private static string _localDbPath = string.Empty;
        private static string _activeConnectionString = string.Empty;
        private static bool _isOnline = false;

        // ── 공개 속성 ────────────────────────────────────

        /// <summary>현재 활성 DB 연결문자열. 모든 View는 이 값을 사용하여 DB에 접속한다.</summary>
        public static string ActiveConnectionString => _activeConnectionString;

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
        /// 앱 시작 시 1회 호출한다. 네트워크 DB 접속을 시도하고,
        /// 실패 시 로컬 DB로 fallback한 후 테이블을 초기화한다.
        /// </summary>
        public static void Initialize()
        {
            // 1. 로컬 DB 경로 계산 (실행폴더\CoA_db\coa_cs.db)
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string localDbFolder = Path.Combine(baseDir, "CoA_db");
            _localDbPath = Path.Combine(localDbFolder, "coa_cs.db");

            // 2. 로컬 DB 폴더 없으면 생성
            if (!Directory.Exists(localDbFolder))
            {
                Directory.CreateDirectory(localDbFolder);
            }

            // 3. 네트워크 DB 접속 시도
            //    네트워크 폴더가 없으면 먼저 생성 시도 (기존 로직 유지)
            string networkFolder = Path.GetDirectoryName(NetworkDbPath);
            if (!string.IsNullOrEmpty(networkFolder) && !Directory.Exists(networkFolder))
            {
                try
                {
                    Directory.CreateDirectory(networkFolder);
                }
                catch
                {
                    // 네트워크 폴더 생성 실패 → 바로 로컬 fallback
                }
            }

            if (TestConnection(NetworkDbPath))
            {
                // 네트워크 접속 성공 → 온라인 모드
                _activeConnectionString = $"Data Source={NetworkDbPath};Default Timeout=10;Pooling=True;";
                _isOnline = true;
            }
            else
            {
                // 네트워크 접속 실패 → 로컬 DB로 fallback
                _activeConnectionString = $"Data Source={_localDbPath};Default Timeout=10;Pooling=True;";
                _isOnline = false;
            }

            // 4. 활성 DB에 테이블 및 인덱스 초기화
            if (!File.Exists(_isOnline ? NetworkDbPath : _localDbPath))
            {
                InitializeTables(_activeConnectionString);
            }
            else
            {
                // 이미 DB 파일이 존재하면 인덱스만 보완 생성
                CreateIndicesForce(_activeConnectionString);
            }

            // 5. 상태 변경 알림 (MainWindow UI 갱신)
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

            // 1. 네트워크 DB 접속 가능 여부 확인
            if (!TestConnection(NetworkDbPath))
            {
                System.Windows.MessageBox.Show(
                    "네트워크 DB에 아직 연결할 수 없습니다.\n네트워크 상태를 확인 후 다시 시도해 주세요.",
                    "연결 실패",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            // 2. 네트워크 DB에 테이블/인덱스 보장 (없으면 생성)
            string networkConnStr = $"Data Source={NetworkDbPath};Default Timeout=10;Pooling=True;";
            if (!File.Exists(NetworkDbPath))
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
    }
}
