using Microsoft.Data.Sqlite;
using System;
using System.Data;
using System.Text;
using System.Windows;
using System.Windows.Controls;

// 🎯 [WPF 전용 매싱 완벽 잠금] MessageBox와 Clipboard 별칭 최종 추가
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using DataGrid = System.Windows.Controls.DataGrid;
using Orientation = System.Windows.Controls.Orientation;
using MessageBox = System.Windows.MessageBox;           // ◀ 추가 (MessageBox 모호성 해결)
using Clipboard = System.Windows.Clipboard;             // ◀ 추가 (Clipboard 모호성 해결)

using Excel = NetOffice.ExcelApi;

namespace CoA_CS
{
    public partial class ItemListView : UserControl
    {
        private DataTable _dtSource = new DataTable();
        private DataGrid _dataGrid = new DataGrid();

        public ItemListView()
        {
            InitializeComponent();
            this.Content = CreateItemListView();
        }

        private UIElement CreateItemListView()
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
                if (e.PropertyName == "pt2_desc1" || e.PropertyName == "pt2_desc2")
                {
                    e.Column.Width = new DataGridLength(280);
                }

                if (e.PropertyName == "pt2_desc1")
                {
                    _dataGrid.FrozenColumnCount = e.Column.DisplayIndex + 1;
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
                    using (var conn = new SqliteConnection(App.ConnectionString))
                    {
                        conn.Open();
                        StringBuilder sql = new StringBuilder();
                        sql.AppendLine("SELECT ");
                        sql.AppendLine("    pt2_domain, pt2_part, pt2_part_type, pt2_desc1, pt2_desc2, "); // 🎯 pt2_domain 컬럼 명시적 조회 추가
                        sql.AppendLine("    pt2_color_code, pt2_um, pt2_alt_um1,  ");
                        sql.AppendLine("    CAST(pt2_conv1 AS TEXT) AS pt2_conv1, ");
                        sql.AppendLine("    CAST(pt2_shelf AS TEXT) AS pt2_shelf, ");
                        sql.AppendLine("    pt2_mfg_part, pt2_prod_line ");
                        sql.AppendLine("FROM pt2_mstr WHERE 1=1");

                        if (!string.IsNullOrEmpty(key1))
                        {
                            sql.AppendLine(@"AND (pt2_part LIKE @likeKey1 
                                               OR pt2_mfg_part LIKE @likeKey1 
                                               OR pt2_desc1 LIKE @likeKey1 
                                               OR pt2_desc2 LIKE @likeKey1)");
                        }

                        if (!string.IsNullOrEmpty(key2))
                        {
                            sql.AppendLine(@"AND (pt2_part LIKE @likeKey2 
                                               OR pt2_mfg_part LIKE @likeKey2 
                                               OR pt2_desc1 LIKE @likeKey2 
                                               OR pt2_desc2 LIKE @likeKey2)");
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

                try
                {
                    using (var conn = new SqliteConnection(App.ConnectionString))
                    {
                        conn.Open();
                        using (var tx = conn.BeginTransaction())
                        {
                            foreach (DataRow row in _dtSource.Rows)
                            {
                                // ① 삭제된 행 처리 (Deleted)
                                if (row.RowState == DataRowState.Deleted)
                                {
                                    // 🎯 pt2_domain + pt2_part 복합 키 조건으로 삭제 타격
                                    string deleteQuery = "DELETE FROM pt2_mstr WHERE pt2_domain = @DomainKey AND pt2_part = @PartKey;";
                                    using (var cmd = new SqliteCommand(deleteQuery, conn, tx))
                                    {
                                        cmd.Parameters.AddWithValue("@DomainKey", row["pt2_domain", DataRowVersion.Original]);
                                        cmd.Parameters.AddWithValue("@PartKey", row["pt2_part", DataRowVersion.Original]);
                                        cmd.ExecuteNonQuery();
                                    }
                                    deleteCount++;
                                }
                                // ② 수정된 행 처리 (Modified)
                                else if (row.RowState == DataRowState.Modified)
                                {
                                    // 🎯 pt2_domain + pt2_part 복합 키 조건으로 업데이트 타격
                                    string updateQuery = @"
                                        UPDATE pt2_mstr 
                                        SET pt2_part_type = @PartType,
                                            pt2_desc1 = @Desc1,
                                            pt2_desc2 = @Desc2,
                                            pt2_color_code = @ColorCode,
                                            pt2_um = @Um,
                                            pt2_alt_um1 = @AltUm1,
                                            pt2_conv1 = @Conv1,
                                            pt2_shelf = @Shelf,
                                            pt2_mfg_part = @MfgPart,
                                            pt2_prod_line = @ProdLine
                                        WHERE pt2_domain = @DomainKey AND pt2_part = @PartKey;";

                                    using (var cmd = new SqliteCommand(updateQuery, conn, tx))
                                    {
                                        cmd.Parameters.AddWithValue("@PartType", row["pt2_part_type"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@Desc1", row["pt2_desc1"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@Desc2", row["pt2_desc2"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@ColorCode", row["pt2_color_code"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@Um", row["pt2_um"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@AltUm1", row["pt2_alt_um1"] ?? DBNull.Value);

                                        double.TryParse(row["pt2_conv1"]?.ToString(), out double cVal);
                                        int.TryParse(row["pt2_shelf"]?.ToString(), out int sVal);

                                        cmd.Parameters.AddWithValue("@Conv1", cVal);
                                        cmd.Parameters.AddWithValue("@Shelf", sVal);
                                        cmd.Parameters.AddWithValue("@MfgPart", row["pt2_mfg_part"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@ProdLine", row["pt2_prod_line"] ?? DBNull.Value);

                                        cmd.Parameters.AddWithValue("@DomainKey", row["pt2_domain", DataRowVersion.Original]);
                                        cmd.Parameters.AddWithValue("@PartKey", row["pt2_part", DataRowVersion.Original]);
                                        cmd.ExecuteNonQuery();
                                    }
                                    updateCount++;
                                }
                                // ③ 새로 추가된 행 처리 (Added)
                                else if (row.RowState == DataRowState.Added)
                                {
                                    // 복합 기본키 누락 방어막
                                    if (row["pt2_domain"] == DBNull.Value || string.IsNullOrEmpty(row["pt2_domain"].ToString())) continue;
                                    if (row["pt2_part"] == DBNull.Value || string.IsNullOrEmpty(row["pt2_part"].ToString())) continue;

                                    string insertQuery = @"
                                        INSERT INTO pt2_mstr (
                                            pt2_domain, pt2_part, pt2_part_type, pt2_desc1, pt2_desc2, 
                                            pt2_color_code, pt2_um, pt2_alt_um1, pt2_conv1, 
                                            pt2_shelf, pt2_mfg_part, pt2_prod_line
                                        ) VALUES (
                                            @Domain, @Part, @PartType, @Desc1, @Desc2, 
                                            @ColorCode, @Um, @AltUm1, @Conv1, 
                                            @Shelf, @MfgPart, @ProdLine
                                        );";

                                    using (var cmd = new SqliteCommand(insertQuery, conn, tx))
                                    {
                                        cmd.Parameters.AddWithValue("@Domain", row["pt2_domain"]);
                                        cmd.Parameters.AddWithValue("@Part", row["pt2_part"]);
                                        cmd.Parameters.AddWithValue("@PartType", row["pt2_part_type"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@Desc1", row["pt2_desc1"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@Desc2", row["pt2_desc2"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@ColorCode", row["pt2_color_code"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@Um", row["pt2_um"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@AltUm1", row["pt2_alt_um1"] ?? DBNull.Value);

                                        double.TryParse(row["pt2_conv1"]?.ToString(), out double cVal);
                                        int.TryParse(row["pt2_shelf"]?.ToString(), out int sVal);

                                        cmd.Parameters.AddWithValue("@Conv1", cVal);
                                        cmd.Parameters.AddWithValue("@Shelf", sVal);
                                        cmd.Parameters.AddWithValue("@MfgPart", row["pt2_mfg_part"] ?? DBNull.Value);
                                        cmd.Parameters.AddWithValue("@ProdLine", row["pt2_prod_line"] ?? DBNull.Value);

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
                        MessageBox.Show($"품목 정보 저장이 완료되었습니다!\n(수정: {updateCount}건, 추가: {insertCount}건, 삭제: {deleteCount}건)", "저장 완료", MessageBoxButton.OK, MessageBoxImage.Information);
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

            // 선택삭제 마우스 이벤트 핸들러
            btnDelete.Click += (s, e) =>
            {
                if (_dataGrid.SelectedItems.Count == 0)
                {
                    MessageBox.Show("삭제할 품목 행을 선택해 주세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (MessageBox.Show("선택한 품목들을 화면에서 제거하시겠습니까?\n(최종 DB 반영은 '저장' 버튼을 누르셔야 처리됩니다.)", "삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
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

            // 아이템 목록 엑셀 화면 직출력 기능
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
                    ws.Name = "ItemList_View";

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

        // 🎯 [수정] 품목 목록 복합키(pt2_domain + pt2_part) 중복 방어형 엑셀 붙여넣기 구역
        private void DataGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.V && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
            {
                e.Handled = true;

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

                    // 🎯 엑셀의 1번째 열을 도메인, 2번째 열을 파트코드로 인지하도록 구성 변경 (그리드 헤더 순서 일치)
                    string excelDomain = cells.Length > 0 ? cells[0].Trim() : "";
                    string excelPart = cells.Length > 1 ? cells[1].Trim() : "";

                    // 💡 방어벽: 만약 엑셀에 도메인이 누락되어 공백으로 들어오면 무조건 기본 'KOR'로 강제 마킹해서 에러 원천 차단!
                    if (string.IsNullOrEmpty(excelDomain)) excelDomain = "KOR";

                    DataRow targetRow = null;

                    if (targetRowIndex < _dtSource.Rows.Count)
                    {
                        targetRow = _dtSource.Rows[targetRowIndex];
                        if (targetRow.RowState == DataRowState.Deleted) continue;
                    }
                    else
                    {
                        // 🔒 [복합키 격파] pt2_domain과 pt2_part가 모두 일치하는 행이 캐시단에 이미 있는지 확인!
                        DataRow[] duplicateRows = _dtSource.Select($"pt2_domain = '{excelDomain}' AND pt2_part = '{excelPart}'");

                        if (duplicateRows.Length > 0)
                        {
                            targetRow = duplicateRows[0]; // 중복 존재 시 새 행을 파지 않고 덮어쓰기 처리!
                            if (targetRow.RowState == DataRowState.Deleted) continue;
                        }
                        else
                        {
                            targetRow = _dtSource.NewRow();
                            _dtSource.Rows.Add(targetRow);
                        }
                    }

                    // 필드 순서 매싱 분출 (복합 키 스펙 반영 주입)
                    if (cells.Length > 0) targetRow["pt2_domain"] = excelDomain;
                    if (cells.Length > 1) targetRow["pt2_part"] = excelPart;
                    if (cells.Length > 2) targetRow["pt2_part_type"] = cells[2].Trim();
                    if (cells.Length > 3) targetRow["pt2_desc1"] = cells[3].Trim();
                    if (cells.Length > 4) targetRow["pt2_desc2"] = cells[4].Trim();
                    if (cells.Length > 5) targetRow["pt2_color_code"] = cells[5].Trim();
                    if (cells.Length > 6) targetRow["pt2_um"] = cells[6].Trim();
                    if (cells.Length > 7) targetRow["pt2_alt_um1"] = cells[7].Trim();
                    if (cells.Length > 8) targetRow["pt2_conv1"] = cells[8].Trim();
                    if (cells.Length > 9) targetRow["pt2_shelf"] = cells[9].Trim();
                    if (cells.Length > 10) targetRow["pt2_mfg_part"] = cells[10].Trim();
                    if (cells.Length > 11) targetRow["pt2_prod_line"] = cells[11].Trim();
                }
            }
        }
    }
}