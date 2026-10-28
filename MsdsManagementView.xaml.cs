using System;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;

namespace QCS
{
    /// <summary>MSDS 조회 및 관리 화면: 기본정보·구성성분 조회 및 선택 레코드 삭제.</summary>
    public partial class MsdsManagementView : System.Windows.Controls.UserControl
    {
        private DataTable _master = new DataTable();

        public MsdsManagementView()
        {
            InitializeComponent();
            Loaded += (_, __) => RefreshData();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshData();

        private static DataTable ReadTable(SqliteConnection connection, string sql, long? id = null)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            if (id.HasValue) cmd.Parameters.AddWithValue("$id", id.Value);
            using var reader = cmd.ExecuteReader();
            var result = new DataTable();
            for (int i = 0; i < reader.FieldCount; i++)
                result.Columns.Add(reader.GetName(i), typeof(string));
            while (reader.Read())
            {
                var row = result.NewRow();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[i] = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i)) ?? "";
                result.Rows.Add(row);
            }
            return result;
        }

        private static SqliteConnection OpenReadOnly()
        {
            string dbPath = DatabaseManager.ActiveDbPath;
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
                throw new FileNotFoundException("QCS DB 파일이 존재하지 않습니다. DB 연결 상태를 확인하세요.", dbPath);
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                DefaultTimeout = 10,
                Pooling = false
            };
            var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            return connection;
        }

        private void RefreshData()
        {
            try
            {
                using var connection = OpenReadOnly();
                _master = ReadTable(connection, "SELECT * FROM msds_mstr ORDER BY msds_msds_id DESC");
                MasterGrid.ItemsSource = _master.DefaultView;
                MasterCount.Text = $"기본정보 ({_master.Rows.Count}건)";
                DetailGrid.ItemsSource = null;
                DetailCount.Text = "구성성분 (0건)";
                StatusText.Text = $"DB: {DatabaseManager.ActiveDbPath} | 마지막 조회: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                if (_master.Rows.Count > 0) MasterGrid.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                StatusText.Text = "DB 조회 실패: " + ex.Message;
                System.Windows.MessageBox.Show(ex.Message, "MSDS DB 조회 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            if (MasterGrid.SelectedItem is not DataRowView row ||
                !long.TryParse(Convert.ToString(row["msds_msds_id"]), out long id))
            {
                System.Windows.MessageBox.Show("삭제할 MSDS를 먼저 선택하세요.", "선택 필요");
                return;
            }

            string name = Convert.ToString(row["msds_file_name"]) ?? "";
            string pdfPath = row.Row.Table.Columns.Contains("msds_file_path")
                ? Convert.ToString(row["msds_file_path"]) ?? "" : "";
            var confirm = System.Windows.MessageBox.Show(
                $"선택한 MSDS를 DB에서 영구 삭제하시겠습니까?\n\nID: {id}\n파일: {name}\n\n" +
                "관련 구성성분·규제·추출 결과 및 등록 이력도 삭제됩니다.\n" +
                "보관된 PDF도 삭제를 시도합니다. PDF 삭제 실패 시 DB 레코드는 삭제됩니다.",
                "MSDS DB 레코드 삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                string dbPath = DatabaseManager.ActiveDbPath;
                if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
                    throw new FileNotFoundException("DB 파일을 찾을 수 없습니다.", dbPath);
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadWrite,
                    DefaultTimeout = 10,
                    Pooling = false
                };
                using var connection = new SqliteConnection(builder.ToString());
                connection.Open();
                using (var pragma = connection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
                    pragma.ExecuteNonQuery();
                }
                using var transaction = connection.BeginTransaction();
                // Child records must be deleted first because the schema uses ON DELETE RESTRICT.
                string[] statements =
                {
                    "DELETE FROM reg_result_dtl WHERE reg_result_msds_id=$id",
                    "DELETE FROM extract_result_dtl WHERE extract_result_msds_id=$id",
                    "DELETE FROM msds_comp_dtl WHERE msds_comp_parent_msds_id=$id",
                    "DELETE FROM msds_oper_dtl WHERE msds_oper_parent_msds_id=$id",
                    "DELETE FROM audit_log_hist WHERE audit_log_msds_id=$id",
                    "DELETE FROM msds_mstr WHERE msds_msds_id=$id"
                };
                int deleted = 0;
                foreach (string sql in statements)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("$id", id);
                    int count = cmd.ExecuteNonQuery();
                    if (sql.StartsWith("DELETE FROM msds_mstr", StringComparison.Ordinal))
                        deleted = count;
                }
                if (deleted != 1)
                    throw new InvalidOperationException("삭제 대상 MSDS가 존재하지 않거나 이미 삭제되었습니다.");
                transaction.Commit();
                string pdfWarning = "";
                if (!string.IsNullOrWhiteSpace(pdfPath))
                {
                    try { if (File.Exists(pdfPath)) File.Delete(pdfPath); }
                    catch (Exception pdfEx) { pdfWarning = " PDF 삭제 실패(수동 삭제 필요): " + pdfEx.Message; }
                }
                RefreshData();
                StatusText.Text = $"MSDS ID {id} DB 레코드 삭제 완료." + pdfWarning;
                if (pdfWarning.Length > 0)
                    System.Windows.MessageBox.Show(pdfWarning, "PDF 삭제 안내",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                StatusText.Text = "DB 레코드 삭제 실패: " + ex.Message;
                System.Windows.MessageBox.Show(ex.Message, "MSDS DB 삭제 오류",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MasterGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MasterGrid.SelectedItem is not DataRowView row) return;
            if (!long.TryParse(Convert.ToString(row["msds_msds_id"]), out long id)) return;
            try
            {
                using var connection = OpenReadOnly();
                var detail = ReadTable(connection,
                    "SELECT * FROM msds_comp_dtl WHERE msds_comp_parent_msds_id = $id ORDER BY msds_comp_sort_order, msds_comp_msds_id", id);
                DetailGrid.ItemsSource = detail.DefaultView;
                DetailCount.Text = $"구성성분 ({detail.Rows.Count}건) — MSDS ID: {id}";
            }
            catch (Exception ex)
            {
                DetailGrid.ItemsSource = null;
                DetailCount.Text = "구성성분 조회 실패";
                StatusText.Text = "구성성분 조회 실패: " + ex.Message;
            }
        }
    }
}
