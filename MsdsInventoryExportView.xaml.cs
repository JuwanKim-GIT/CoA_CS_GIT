using System;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Data.Sqlite;
using UserControl = System.Windows.Controls.UserControl;
using MessageBox = System.Windows.MessageBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace QCS
{
    public partial class MsdsInventoryExportView : UserControl
    {
        private DataTable _data = new DataTable();

        public MsdsInventoryExportView()
        {
            InitializeComponent();
            Loaded += (_, _) => RefreshData();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshData();
        private void Search_Click(object sender, RoutedEventArgs e) => ApplyFilter();

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) ApplyFilter();
        }

        private void RefreshData()
        {
            try
            {
                string path = DatabaseManager.ActiveDbPath;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    throw new FileNotFoundException("QCS DB 파일이 없습니다.", path);

                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                    DefaultTimeout = 10,
                    Pooling = false
                };
                using var connection = new SqliteConnection(builder.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"SELECT msds_msds_id, msds_graco_code, msds_product_name,
                                              msds_supplier_name, msds_revision_date, msds_is_latest
                                       FROM msds_mstr
                                       WHERE COALESCE(msds_is_deleted, 0) = 0
                                       ORDER BY msds_graco_code, msds_revision_date DESC, msds_msds_id DESC";
                using var reader = command.ExecuteReader();
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
                _data = result;
                InventoryGrid.ItemsSource = _data.DefaultView;
                ApplyFilter();
                StatusText.Text = $"DB: {path} | 조회: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            }
            catch (Exception ex)
            {
                StatusText.Text = "조회 오류: " + ex.Message;
                MessageBox.Show(ex.Message, "RM Inventory 조회 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplyFilter()
        {
            if (_data.Columns.Count == 0) return;
            static string Escape(string text) => text.Replace("'", "''").Replace("[", "[[]")
                .Replace("%", "[%]").Replace("*", "[*]");
            var filters = new System.Collections.Generic.List<string>();
            string term = SearchBox.Text.Trim();
            string supplier = SupplierBox.Text.Trim();
            if (term.Length > 0)
            {
                string safe = Escape(term);
                filters.Add($"(msds_graco_code LIKE '%{safe}%' OR msds_product_name LIKE '%{safe}%')");
            }
            if (supplier.Length > 0)
                filters.Add($"msds_supplier_name LIKE '%{Escape(supplier)}%'");
            if (LatestOnly.IsChecked == true)
                filters.Add("msds_is_latest = '1'");
            _data.DefaultView.RowFilter = string.Join(" AND ", filters);
            CountText.Text = $"MSDS 목록 ({_data.DefaultView.Count}건)";
            InventoryGrid.SelectedIndex = _data.DefaultView.Count > 0 ? 0 : -1;
            if (_data.DefaultView.Count == 0) ClearSelection();
        }

        private void ClearSelection()
        {
            SelectedName.Text = "선택된 항목 없음";
            IngredientCount.Text = "-";
            WarningText.Text = "조회할 MSDS를 선택하세요.";
        }

        private void InventoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (InventoryGrid.SelectedItem is not DataRowView row)
            {
                ClearSelection();
                return;
            }
            string idText = Convert.ToString(row["msds_msds_id"]) ?? "";
            SelectedName.Text = $"{row["msds_graco_code"]} / {row["msds_product_name"]}";
            if (!long.TryParse(idText, out long id)) return;
            try
            {
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = DatabaseManager.ActiveDbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    DefaultTimeout = 10,
                    Pooling = false
                };
                using var connection = new SqliteConnection(builder.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM msds_comp_dtl WHERE msds_comp_parent_msds_id = $id";
                command.Parameters.AddWithValue("$id", id);
                long count = Convert.ToInt64(command.ExecuteScalar());
                IngredientCount.Text = $"{count}건";
                WarningText.Text = count == 0
                    ? "주의: 구성성분이 등록되어 있지 않습니다."
                    : "기본 점검 통과: 구성성분이 존재합니다. 42열/규제정보 검증은 후속 구현 대상입니다.";
            }
            catch (Exception ex)
            {
                IngredientCount.Text = "조회 실패";
                WarningText.Text = ex.Message;
            }
        }
    }
}
