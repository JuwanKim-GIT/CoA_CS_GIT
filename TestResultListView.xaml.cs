using Microsoft.Data.Sqlite;
using System;
using System.Data;
using System.Text;
using System.Windows;
using System.Windows.Controls;

// 🎯 [WPF 전용 매싱 완벽 잠금] 마지막 남은 에러 3개를 저격하기 위한 최종 별칭 추가
using UserControl = System.Windows.Controls.UserControl;
using DataGrid = System.Windows.Controls.DataGrid;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;   // ◀ 추가 (Orientation 에러 해결)
using DataGridCell = System.Windows.Controls.DataGridCell; // ◀ 추가 (DataGridCell 에러 해결)
using Clipboard = System.Windows.Clipboard;               // ◀ 추가 (Clipboard 에러 해결)

using Excel = NetOffice.ExcelApi;

namespace CoA_CS
{
    public partial class TestResultListView : UserControl
    {
        private DataTable _dtSource = new DataTable();
        private DataGrid _dataGrid = new DataGrid();

        public TestResultListView()
        {
            InitializeComponent();
            this.Content = CreateTestResultListView();
        }

        private UIElement CreateTestResultListView()
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

                if (colName == "no")
                {
                    e.Column.Width = new DataGridLength(50);
                }
                else if (colName == "Characteristic")
                {
                    e.Column.Width = new DataGridLength(350);
                }
                else if (colName == "UoM")
                {
                    e.Column.Width = new DataGridLength(60);
                }
                else if (colName == "First Value" || colName == "Last Value" || colName == "Lower Limit" || colName == "Upper Limit")
                {
                    e.Column.Width = new DataGridLength(100);

                    var rightAlignStyle = new Style(typeof(DataGridCell));
                    rightAlignStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
                    e.Column.CellStyle = rightAlignStyle;
                }
            };

            // 🎯 [이벤트 연결] 엑셀 대량 붙여넣기 감지기 드디어 장착!
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
                    using (var conn = new SqliteConnection(App.ConnectionString))
                    {
                        conn.Open();

                        StringBuilder sql = new StringBuilder();
                        sql.AppendLine("SELECT ");
                        sql.AppendLine("    qmir_batch AS [batch no], ");
                        sql.AppendLine("    qmir_no AS [no], ");
                        sql.AppendLine("    qmir_charac AS [Characteristic], ");
                        sql.AppendLine("    printf('%.5f', qmir_first_value) AS [First Value], ");
                        sql.AppendLine("    printf('%.5f', qmir_last_value) AS [Last Value], ");
                        sql.AppendLine("    printf('%.5f', qmir_ltol) AS [Lower Limit], ");
                        sql.AppendLine("    printf('%.5f', qmir_utol) AS [Upper Limit], ");
                        sql.AppendLine("    qmir_uom AS [UoM], ");
                        sql.AppendLine("    qmir_part AS [part], ");
                        sql.AppendLine("    qmir_mf_date AS [mfdate] ");
                        sql.AppendLine("FROM qmir_det WHERE 1=1");

                        if (!string.IsNullOrEmpty(key1))
                        {
                            sql.AppendLine(@"AND (qmir_batch LIKE @likeKey1 
                                               OR qmir_charac LIKE @likeKey1 
                                               OR qmir_part LIKE @likeKey1)");
                        }

                        if (!string.IsNullOrEmpty(key2))
                        {
                            sql.AppendLine(@"AND (qmir_batch LIKE @likeKey2 
                                               OR qmir_charac LIKE @likeKey2 
                                               OR qmir_part LIKE @likeKey2)");
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

            // 데이터 일괄 저장 로직
            Action ActionSave = () =>
            {
                _dataGrid.CommitEdit(DataGridEditingUnit.Row, true);

                int updateCount = 0;
                int insertCount = 0;
                int deleteCount = 0;

                object ToDbDouble(object value)
                {
                    if (value == DBNull.Value || value == null) return DBNull.Value;
                    if (double.TryParse(value.ToString(), out double res)) return res;
                    return DBNull.Value;
                }

                try
                {
                    using (var conn = new SqliteConnection(App.ConnectionString))
                    {
                        conn.Open();
                        using (var tx = conn.BeginTransaction())
                        {
                            foreach (DataRow row in _dtSource.Rows)
                            {
                                if (row.RowState == DataRowState.Deleted)
                                {
                                    string deleteQuery = "DELETE FROM qmir_det WHERE qmir_batch = @BatchKey AND qmir_no = @NoKey;";
                                    using (var cmd = new SqliteCommand(deleteQuery, conn, tx))
                                    {
                                        cmd.Parameters.AddWithValue("@BatchKey", row["batch no", DataRowVersion.Original]);
                                        cmd.Parameters.AddWithValue("@NoKey", row["no", DataRowVersion.Original]);
                                        cmd.ExecuteNonQuery();
                                    }
                                    deleteCount++;
                                }
                                else if (row.RowState == DataRowState.Modified)
                                {
                                    string updateQuery = @"
                                        UPDATE qmir_det 
                                        SET qmir_charac = @Charac,
                                            qmir_first_value = @FirstVal,
                                            qmir_last_value = @LastVal,
                                            qmir_ltol = @Ltol,
                                            qmir_utol = @Utol,
                                            qmir_uom = @Uom,
                                            qmir_part = @Part,
                                            qmir_mf_date = @MfDate
                                        WHERE qmir_batch = @BatchKey AND qmir_no = @NoKey;";

                                    using (var cmd = new SqliteCommand(updateQuery, conn, tx))
                                    {
                                        cmd.Parameters.AddWithValue("@Charac", row["Characteristic"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@FirstVal", ToDbDouble(row["First Value"]));
                                        cmd.Parameters.AddWithValue("@LastVal", ToDbDouble(row["Last Value"]));
                                        cmd.Parameters.AddWithValue("@Ltol", ToDbDouble(row["Lower Limit"]));
                                        cmd.Parameters.AddWithValue("@Utol", ToDbDouble(row["Upper Limit"]));
                                        cmd.Parameters.AddWithValue("@Uom", row["UoM"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@Part", row["part"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@MfDate", row["mfdate"] ?? DBNull.Value);

                                        cmd.Parameters.AddWithValue("@BatchKey", row["batch no", DataRowVersion.Original]);
                                        cmd.Parameters.AddWithValue("@NoKey", row["no", DataRowVersion.Original]);
                                        cmd.ExecuteNonQuery();
                                    }
                                    updateCount++;
                                }
                                else if (row.RowState == DataRowState.Added)
                                {
                                    if (row["batch no"] == DBNull.Value || string.IsNullOrEmpty(row["batch no"].ToString())) continue;
                                    if (row["no"] == DBNull.Value || string.IsNullOrEmpty(row["no"].ToString())) continue;

                                    string insertQuery = @"
                                        INSERT INTO qmir_det (
                                            qmir_batch, qmir_no, qmir_charac, qmir_first_value, 
                                            qmir_last_value, qmir_ltol, qmir_utol, qmir_uom, 
                                            qmir_part, qmir_mf_date
                                        ) VALUES (
                                            @Batch, @No, @Charac, @FirstVal, 
                                            @LastVal, @Ltol, @Utol, @Uom, 
                                            @Part, @MfDate
                                        );";

                                    using (var cmd = new SqliteCommand(insertQuery, conn, tx))
                                    {
                                        cmd.Parameters.AddWithValue("@Batch", row["batch no"]);
                                        cmd.Parameters.AddWithValue("@No", row["no"]);
                                        cmd.Parameters.AddWithValue("@Charac", row["Characteristic"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@FirstVal", ToDbDouble(row["First Value"]));
                                        cmd.Parameters.AddWithValue("@LastVal", ToDbDouble(row["Last Value"]));
                                        cmd.Parameters.AddWithValue("@Ltol", ToDbDouble(row["Lower Limit"]));
                                        cmd.Parameters.AddWithValue("@Utol", ToDbDouble(row["Upper Limit"]));
                                        cmd.Parameters.AddWithValue("@Uom", row["UoM"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@Part", row["part"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@MfDate", row["mfdate"] ?? DBNull.Value);
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
                        MessageBox.Show($"실험 결과 저장이 완료되었습니다!\n(수정: {updateCount}건, 추가: {insertCount}건, 삭제: {deleteCount}건)", "저장 완료", MessageBoxButton.OK, MessageBoxImage.Information);
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

            // 선택삭제 마우스 이벤트 결합
            btnDelete.Click += (s, e) =>
            {
                if (_dataGrid.SelectedItems.Count == 0)
                {
                    MessageBox.Show("삭제할 결과 행을 선택해 주세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            // 엑셀 화면 직출력 기능
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
                    ws.Name = "TestResult_View";

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

            ActionSearch();

            btnSearch.Click += (s, e) => ActionSearch();
            btnSave.Click += (s, e) => ActionSave();
            txtSearch1.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) ActionSearch(); };
            txtSearch2.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) ActionSearch(); };

            return mainPanel;
        }

        // 🎯 [핵심 기능] Ctrl+V 포착 복합키(batch no + no) 중복 방어형 엑셀 붙여넣기 구역
        private void DataGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.V && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
            {
                e.Handled = true; // WPF 순정 붙여넣기 차단막 가동

                string clipboardText = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(clipboardText)) return;

                int startRowIndex = _dataGrid.SelectedIndex;
                if (startRowIndex < 0 && _dataGrid.CurrentItem != null)
                {
                    startRowIndex = _dataGrid.Items.IndexOf(_dataGrid.CurrentItem);
                }
                if (startRowIndex < 0) startRowIndex = _dtSource.Rows.Count;

                string[] lines = clipboardText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    string[] cells = line.Split('\t');
                    int targetRowIndex = startRowIndex + i;

                    string excelBatch = cells.Length > 0 ? cells[0].Trim() : "";
                    string excelNo = cells.Length > 1 ? cells[1].Trim() : "";

                    DataRow targetRow = null;

                    if (targetRowIndex < _dtSource.Rows.Count)
                    {
                        targetRow = _dtSource.Rows[targetRowIndex];
                        if (targetRow.RowState == DataRowState.Deleted) continue;
                    }
                    else
                    {
                        // 🔒 [치명적인 약점 격파] 복합키 중복이 있는지 캐시단에서 정밀 검사 수행
                        DataRow[] duplicateRows = _dtSource.Select($"[batch no] = '{excelBatch}' AND [no] = '{excelNo}'");

                        if (duplicateRows.Length > 0)
                        {
                            targetRow = duplicateRows[0]; // 중복 발생 시 새 행을 파지 않고 기존 행 덮어쓰기로 우회!
                            if (targetRow.RowState == DataRowState.Deleted) continue;
                        }
                        else
                        {
                            targetRow = _dtSource.NewRow();
                            _dtSource.Rows.Add(targetRow);
                        }
                    }

                    // 정석 필드 매싱 분출 (총 10개 컬럼 스펙 순서 일치)
                    if (cells.Length > 0) targetRow["batch no"] = excelBatch;
                    if (cells.Length > 1) targetRow["no"] = excelNo;
                    if (cells.Length > 2) targetRow["Characteristic"] = cells[2].Trim();
                    if (cells.Length > 3) targetRow["First Value"] = cells[3].Trim();
                    if (cells.Length > 4) targetRow["Last Value"] = cells[4].Trim();
                    if (cells.Length > 5) targetRow["Lower Limit"] = cells[5].Trim();
                    if (cells.Length > 6) targetRow["Upper Limit"] = cells[6].Trim();
                    if (cells.Length > 7) targetRow["UoM"] = cells[7].Trim();
                    if (cells.Length > 8) targetRow["part"] = cells[8].Trim();
                    if (cells.Length > 9) targetRow["mfdate"] = cells[9].Trim();
                }
            }
        }
    }
}