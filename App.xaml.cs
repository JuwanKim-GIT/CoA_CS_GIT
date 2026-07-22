using System;
using System.IO;
using System.Windows;
using Microsoft.Data.Sqlite;

namespace CoA_CS
{
    public partial class App : System.Windows.Application
    {
        private static string _dbPath = string.Empty;

        // 🎯 다른 클래스나 윈도우에서 DB 연결할 때 언제나 이 값을 가져다 쓰도록 공개!
        public static string ConnectionString { get; private set; } = string.Empty;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            string dbFolder = @"\\zcnpo0317vn0001\IPK_DBs\db_files";

            if (!Directory.Exists(dbFolder))
            {
                Directory.CreateDirectory(dbFolder);
            }

            _dbPath = Path.Combine(dbFolder, "coa_cs.db");

            // 🎯 Pooling=True; 옵션을 유지하여 기본 통로 재사용 가동
            ConnectionString = $"Data Source={_dbPath};Default Timeout=10;Pooling=True;";

            // 1. 파일이 없으면 전체 테이블 및 인덱스 신규 초기화
            if (!File.Exists(_dbPath))
            {
                _initializeTables();
            }
            else
            {
                // 2. [강제 주입 보안관 🎯] 기존 DB가 이미 있어도 인덱스만 따로 완벽하게 보완 생성!
                _createIndicesForce();
            }
        }

        private void _initializeTables()
        {
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(App.ConnectionString))
            {
                conn.Open();
                using (var cmd = new Microsoft.Data.Sqlite.SqliteCommand())
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

            // 첫 가동 시에도 인덱스 자동 생성 탈 수 있도록 호출 연계
            _createIndicesForce();
        }

        /// <summary>
        /// ⚡ 기존 DB 커스텀 인덱스 강제 빌드 엔진
        /// </summary>
        private void _createIndicesForce()
        {
            try
            {
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(App.ConnectionString))
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