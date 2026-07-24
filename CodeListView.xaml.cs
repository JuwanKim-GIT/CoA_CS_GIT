using Microsoft.Data.Sqlite;
using System;
using System.Data;
using System.Text;
using System.Windows;
using System.Windows.Controls;

// 🎯 [WPF 전용 매싱 완벽 잠금] 누락된 핵심 3가지 별칭 추가로 에러 올킬
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using DataGrid = System.Windows.Controls.DataGrid;
using MessageBox = System.Windows.MessageBox;           // ◀ 추가 (MessageBox 모호성 해결)
using Clipboard = System.Windows.Clipboard;             // ◀ 추가 (Clipboard 모호성 해결)
using Orientation = System.Windows.Controls.Orientation; // ◀ 추가 (Orientation 모호성 해결)

using Excel = NetOffice.ExcelApi;

namespace CoA_CS
{
    public partial class CodeListView : UserControl
    {
        private DataTable _dtSource = new DataTable();
        private DataGrid _dataGrid = new DataGrid();

        public CodeListView()
        {
            InitializeComponent();
            this.Content = CreateCodeListView();
        }

        private UIElement CreateCodeListView()
        {
            DockPanel mainPanel = new DockPanel();

            StackPanel searchBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(10, 10, 10, 5)
            };

            TextBlock lblSearch1 = new TextBlock
            {
                Text = "검색어 1:",
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.DimGray
            };
            TextBox txtSearch1 = new TextBox
            {
                Width = 150,
                Height = 26,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 15, 0)
            };

            TextBlock lblSearch2 = new TextBlock
            {
                Text = "검색어 2:",
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.DimGray
            };
            TextBox txtSearch2 = new TextBox
            {
                Width = 150,
                Height = 26,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 15, 0)
            };

            Button btnSearch = new Button { Content = "검색", Width = 80, Height = 26, Margin = new Thickness(0, 0, 10, 0) };
            Button btnDelete = new Button { Content = "선택삭제", Width = 80, Height = 26, Margin = new Thickness(0, 0, 10, 0), Background = System.Windows.Media.Brushes.MistyRose };
            Button btnSave = new Button { Content = "저장", Width = 80, Height = 26, Margin = new Thickness(0, 0, 10, 0), Background = System.Windows.Media.Brushes.LightBlue, FontWeight = FontWeights.Bold };
            Button btnExport = new Button { Content = "엑셀출력", Width = 80, Height = 26, Background = System.Windows.Media.Brushes.LightGreen, FontWeight = FontWeights.Bold };

            searchBar.Children.Add(lblSearch1);
            searchBar.Children.Add(txtSearch1);
            searchBar.Children.Add(lblSearch2);
            searchBar.Children.Add(txtSearch2);
            searchBar.Children.Add(btnSearch);
            searchBar.Children.Add(btnDelete);
            searchBar.Children.Add(btnSave);
            searchBar.Children.Add(btnExport);

            DockPanel.SetDock(searchBar, Dock.Top);
            mainPanel.Children.Add(searchBar);

            _dataGrid = new DataGrid
            {
                Margin = new Thickness(10, 5, 10, 10),
                IsReadOnly = false,
                CanUserAddRows = true,
                CanUserDeleteRows = true,
                AutoGenerateColumns = true,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                Background = System.Windows.Media.Brushes.White,
                ColumnWidth = new DataGridLength(1, DataGridLengthUnitType.Auto),

                SelectionMode = DataGridSelectionMode.Extended,
                SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
                ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader
            };

            _dataGrid.AutoGeneratingColumn += (s, e) =>
            {
                string colName = e.PropertyName;
                switch (colName)
                {
                    case "Field Name": e.Column.Width = new DataGridLength(180); break;
                    case "Code Value": e.Column.Width = new DataGridLength(180); break;
                    case "Comment": e.Column.Width = new DataGridLength(340); break;
                    case "Description1": e.Column.Width = new DataGridLength(340); break;
                    case "Description2": e.Column.Width = new DataGridLength(340); break;
                    default: e.Column.Width = DataGridLength.Auto; break;
                }
            };

            _dataGrid.PreviewKeyDown += DataGrid_PreviewKeyDown;

            mainPanel.Children.Add(_dataGrid);

            // 데이터 조회 로직
            Action ActionSearch = () =>
            {
                _dtSource = new DataTable();
                string key1 = txtSearch1.Text.Trim();
                string key2 = txtSearch2.Text.Trim();

                try
                {
                    using (var conn = new SqliteConnection(DatabaseManager.ActiveConnectionString))
                    {
                        conn.Open();
                        StringBuilder sql = new StringBuilder();
                        sql.AppendLine("SELECT ");
                        sql.AppendLine("    zx_code_fldname AS [Field Name], ");
                        sql.AppendLine("    zx_code_value AS [Code Value], ");
                        sql.AppendLine("    zx_code_cmmt  AS [Comment], ");
                        sql.AppendLine("    zx_code_desc1 AS [Description1], ");
                        sql.AppendLine("    zx_code_desc2 AS [Description2], ");
                        sql.AppendLine("    zx_code_char1 AS [Char#1], ");
                        sql.AppendLine("    zx_code_char2 AS [Char#2] ");
                        sql.AppendLine("FROM zx_code_mstr WHERE 1=1");

                        if (!string.IsNullOrEmpty(key1))
                        {
                            sql.AppendLine(@"AND (zx_code_fldname LIKE @likeKey1 
                                               OR zx_code_value LIKE @likeKey1 
                                               OR zx_code_cmmt LIKE @likeKey1 
                                               OR zx_code_desc1 LIKE @likeKey1 
                                               OR zx_code_desc2 LIKE @likeKey1)");
                        }

                        if (!string.IsNullOrEmpty(key2))
                        {
                            sql.AppendLine(@"AND (zx_code_fldname LIKE @likeKey2 
                                               OR zx_code_value LIKE @likeKey2 
                                               OR zx_code_cmmt LIKE @likeKey2 
                                               OR zx_code_desc1 LIKE @likeKey2 
                                               OR zx_code_desc2 LIKE @likeKey2)");
                        }

                        using (var cmd = new SqliteCommand(sql.ToString(), conn))
                        {
                            if (!string.IsNullOrEmpty(key1)) cmd.Parameters.AddWithValue("@likeKey1", $"%{key1}%");
                            if (!string.IsNullOrEmpty(key2)) cmd.Parameters.AddWithValue("@likeKey2", $"%{key2}%");

                            using (var reader = cmd.ExecuteReader())
                            {
                                _dtSource.Load(reader);
                            }
                        }
                    }

                    _dataGrid.ItemsSource = null;
                    _dataGrid.ItemsSource = _dtSource.DefaultView;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"데이터 조회 중 에러 발생:\n{ex.Message}", "조회 오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            // 데이터 일괄 저장 (CUD 통합 처리) 로직
            Action ActionSave = () =>
            {
                _dataGrid.CommitEdit(DataGridEditingUnit.Row, true);

                int updateCount = 0;
                int insertCount = 0;
                int deleteCount = 0;

                try
                {
                    using (var conn = new SqliteConnection(DatabaseManager.ActiveConnectionString))
                    {
                        conn.Open();
                        using (var tx = conn.BeginTransaction())
                        {
                            foreach (DataRow row in _dtSource.Rows)
                            {
                                if (row.RowState == DataRowState.Deleted)
                                {
                                    string deleteQuery = "DELETE FROM zx_code_mstr WHERE zx_code_fldname = @FieldName AND zx_code_value = @CodeValue;";
                                    using (var cmd = new SqliteCommand(deleteQuery, conn, tx))
                                    {
                                        cmd.Parameters.AddWithValue("@FieldName", row["Field Name", DataRowVersion.Original]);
                                        cmd.Parameters.AddWithValue("@CodeValue", row["Code Value", DataRowVersion.Original]);
                                        cmd.ExecuteNonQuery();
                                    }
                                    deleteCount++;
                                }
                                else if (row.RowState == DataRowState.Modified)
                                {
                                    string updateQuery = @"
                                        UPDATE zx_code_mstr 
                                        SET zx_code_cmmt = @Comment,
                                            zx_code_desc1 = @Desc1,
                                            zx_code_desc2 = @Desc2,
                                            zx_code_char1 = @Char1,
                                            zx_code_char2 = @Char2
                                        WHERE zx_code_fldname = @FieldName AND zx_code_value = @CodeValue;";

                                    using (var cmd = new SqliteCommand(updateQuery, conn, tx))
                                    {
                                        cmd.Parameters.AddWithValue("@Comment", row["Comment"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@Desc1", row["Description1"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@Desc2", row["Description2"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@Char1", row["Char#1"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@Char2", row["Char#2"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@FieldName", row["Field Name", DataRowVersion.Original]);
                                        cmd.Parameters.AddWithValue("@CodeValue", row["Code Value", DataRowVersion.Original]);
                                        cmd.ExecuteNonQuery();
                                    }
                                    updateCount++;
                                }
                                else if (row.RowState == DataRowState.Added)
                                {
                                    if (row["Field Name"] == DBNull.Value || string.IsNullOrEmpty(row["Field Name"].ToString())) continue;

                                    string insertQuery = @"
                                        INSERT INTO zx_code_mstr (zx_code_fldname, zx_code_value, zx_code_cmmt, zx_code_desc1, zx_code_desc2, zx_code_char1, zx_code_char2)
                                        VALUES (@FieldName, @CodeValue, @Comment, @Desc1, @Desc2, @Char1, @Char2);";

                                    using (var cmd = new SqliteCommand(insertQuery, conn, tx))
                                    {
                                        cmd.Parameters.AddWithValue("@FieldName", row["Field Name"]);
                                        cmd.Parameters.AddWithValue("@CodeValue", row["Code Value"] ?? "");
                                        cmd.Parameters.AddWithValue("@Comment", row["Comment"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@Desc1", row["Description1"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@Desc2", row["Description2"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@Char1", row["Char#1"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@Char2", row["Char#2"] ?? DBNull.Value);
                                        cmd.ExecuteNonQuery();
                                    }
                                    insertCount++;
                                }
                            }
                            tx.Commit();
                        }
                    }

                    if (updateCount > 0 || insertCount > 0 || deleteCount > 0)
                    {
                        MessageBox.Show($"DB 저장이 정상 완료되었습니다!\n(수정: {updateCount}건, 추가: {insertCount}건, 삭제: {deleteCount}건)", "저장 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                        ActionSearch();
                    }
                    else
                    {
                        MessageBox.Show("변경된 데이터가 없습니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"데이터 저장 중 오류가 발생했습니다:\n{ex.Message}", "저장 오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            btnDelete.Click += (s, e) =>
            {
                if (_dataGrid.SelectedItems.Count == 0)
                {
                    MessageBox.Show("삭제할 행을 그리드에서 선택해 주세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (MessageBox.Show("선택한 행들을 화면에서 제거하시겠습니까?\n(최종 DB 반영은 '저장' 버튼을 누르셔야 처리됩니다.)", "삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    System.Collections.ArrayList selectedList = new System.Collections.ArrayList(_dataGrid.SelectedItems);
                    foreach (var item in selectedList)
                    {
                        if (item is DataRowView rowView)
                        {
                            rowView.Row.Delete();
                        }
                    }
                }
            };

            // 파일 저장 없이 화면에 바로 엑셀 띄우기 (Export to Direct View)
            btnExport.Click += (s, e) =>
            {
                if (_dtSource == null || _dtSource.Rows.Count == 0)
                {
                    MessageBox.Show("출력할 데이터가 없습니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Excel.Application excelApp = null;
                Excel.Workbook wb = null;
                Excel.Worksheet ws = null;

                try
                {
                    excelApp = new Excel.Application();
                    excelApp.Visible = false;
                    excelApp.DisplayAlerts = false;

                    wb = excelApp.Workbooks.Add();
                    ws = (Excel.Worksheet)wb.Sheets[1];
                    ws.Name = "CodeList_View";

                    int colCount = _dtSource.Columns.Count;
                    for (int c = 0; c < colCount; c++)
                    {
                        ((Excel.Range)ws.Cells[1, c + 1]).Value = _dtSource.Columns[c].ColumnName;
                    }

                    int rowCount = _dtSource.Rows.Count;
                    int excelRowIdx = 2;

                    for (int r = 0; r < rowCount; r++)
                    {
                        DataRow row = _dtSource.Rows[r];
                        if (row.RowState == DataRowState.Deleted) continue;

                        for (int c = 0; c < colCount; c++)
                        {
                            ((Excel.Range)ws.Cells[excelRowIdx, c + 1]).Value = row[c]?.ToString() ?? "";
                        }
                        excelRowIdx++;
                    }

                    excelApp.Visible = true;
                    excelApp.DisplayAlerts = true;

                    wb = null;
                    excelApp = null;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"엑셀을 화면에 띄우는 중 오류가 발생했습니다:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                    if (wb != null) { wb.Dispose(); wb = null; }
                    if (excelApp != null) { excelApp.Quit(); excelApp.Dispose(); excelApp = null; }
                }
            };

            btnSearch.Click += (s, e) => ActionSearch();
            btnSave.Click += (s, e) => ActionSave();
            txtSearch1.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) ActionSearch(); };
            txtSearch2.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) ActionSearch(); };

            ActionSearch();

            return mainPanel;
        }

        // 🎯 [핵심 기능] 시작 열(Column) 인덱스 추적 및 단일/대량 복사-붙여넣기 완벽 대응
        private void DataGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.V && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
            {
                e.Handled = true; // WPF 순정 기본 셀 붙여넣기 차단

                string clipboardText = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(clipboardText)) return;

                // 1. 현재 선택된 행(Row) 및 열(Column) 시작 위치 추적
                int startRowIndex = _dataGrid.SelectedIndex;
                if (startRowIndex < 0 && _dataGrid.CurrentItem != null)
                {
                    startRowIndex = _dataGrid.Items.IndexOf(_dataGrid.CurrentItem);
                }
                if (startRowIndex < 0) startRowIndex = _dtSource.Rows.Count;

                int startColumnIndex = 0;
                if (_dataGrid.CurrentCell != null && _dataGrid.CurrentCell.Column != null)
                {
                    startColumnIndex = _dataGrid.CurrentCell.Column.DisplayIndex;
                }

                // 2. 클립보드 줄바꿈 처리
                string[] lines = clipboardText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line) && i == lines.Length - 1) continue; // 마지막 빈 줄 방어

                    string[] cells = line.Split('\t');
                    int targetRowIndex = startRowIndex + i;

                    DataRow targetRow = null;

                    // Target 행 찾기 또는 새 행 생성
                    if (targetRowIndex < _dtSource.Rows.Count)
                    {
                        targetRow = _dtSource.Rows[targetRowIndex];
                        if (targetRow.RowState == DataRowState.Deleted) continue;
                    }
                    else
                    {
                        targetRow = _dtSource.NewRow();
                        _dtSource.Rows.Add(targetRow);
                    }

                    // 3. 선택한 열 위치부터 순서대로 매핑하여 붙여넣기
                    for (int j = 0; j < cells.Length; j++)
                    {
                        int targetColumnIndex = startColumnIndex + j;

                        // DataTable 컬럼 범위를 벗어나지 않도록 방어
                        if (targetColumnIndex < _dtSource.Columns.Count)
                        {
                            string colName = _dtSource.Columns[targetColumnIndex].ColumnName;
                            targetRow[colName] = cells[j].Trim();
                        }
                    }
                }
            }
        }
    }
}