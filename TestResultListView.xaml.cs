using Microsoft.Data.Sqlite;
using System;
using System.Data;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

using DataFormats = System.Windows.DataFormats;

// 🎯 [WPF 전용 매싱 완벽 잠금] 마지막 남은 에러 3개를 저격하기 위한 최종 별칭 추가
using UserControl = System.Windows.Controls.UserControl;
using DataGrid = System.Windows.Controls.DataGrid;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Binding = System.Windows.Data.Binding;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;   // ◀ 추가 (Orientation 에러 해결)
using DataGridCell = System.Windows.Controls.DataGridCell; // ◀ 추가 (DataGridCell 에러 해결)
using Clipboard = System.Windows.Clipboard;               // ◀ 추가 (Clipboard 에러 해결)

using Excel = NetOffice.ExcelApi;

namespace QCS
{
    public partial class TestResultListView : UserControl
    {
        private DataTable _dtSource = new DataTable();
        private DataGrid _dataGrid = new DataGrid();
        private readonly Dictionary<string, Dictionary<string, object>> _batchPending = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        private static string RecordKey(DataRow r) => Convert.ToString(r["batch no", r.RowState == DataRowState.Deleted ? DataRowVersion.Original : DataRowVersion.Current]) + "\u001f" + Convert.ToString(r["no", r.RowState == DataRowState.Deleted ? DataRowVersion.Original : DataRowVersion.Current]);
        private static bool SameValue(object a, object b, string name)
        {
            if (name == "part") return (Convert.ToString(a) ?? "").TrimStart('0') == (Convert.ToString(b) ?? "").TrimStart('0');
            return string.Equals(Convert.ToString(a), Convert.ToString(b), StringComparison.Ordinal);
        }
        private static bool IsChanged(DataRow r)
        {
            if (r.RowState != DataRowState.Modified) return false;
            foreach (DataColumn col in r.Table.Columns)
                if (!SameValue(r[col, DataRowVersion.Original], r[col], col.ColumnName)) return true;
            return false;
        }
        private void NormalizeRevertedRows()
        {
            foreach (DataRow r in _dtSource.Rows)
                if (r.RowState == DataRowState.Modified && !IsChanged(r)) r.AcceptChanges();
        }
        public TestResultListView()
        {
            InitializeComponent();
            this.Content = CreateTestResultListView();
        }

        // 기존 메인 상태표시줄을 사용하며, 긴 동기 조회 직전 메시지를 화면에 반영합니다.
        private void ReportLoadStatus(string message)
        {
            if (System.Windows.Window.GetWindow(this) is MainWindow main)
            {
                main.SetRunStatus(message);
                Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));
            }
        }

        private UIElement CreateTestResultListView()
        {
            DockPanel mainPanel = new DockPanel();

            // 검색/작업 영역: 기존 그리드 및 DB 편집 로직과 독립적으로 구성
            var outerBar = new StackPanel { Margin = new Thickness(10, 10, 10, 5) };
            var searchBar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 7) };
            var actionBar = new DockPanel { LastChildFill = true };
            var actionButtons = new WrapPanel { Orientation = Orientation.Horizontal };
            TextBlock lblBatch = new TextBlock { Text = "Batch No:", VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.DimGray };
            TextBox txtBatch = new TextBox { Width = 145, Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 12, 4) };
            TextBlock lblPart = new TextBlock { Text = "Material No:", VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.DimGray };
            TextBox txtPart = new TextBox { Width = 145, Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 12, 4) };
            TextBlock lblMfDate = new TextBlock { Text = "Manufacturing Date:", VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.DimGray };
            DatePicker dateFrom = new DatePicker { Width = 135, Height = 28, Margin = new Thickness(6, 0, 4, 4) };
            TextBlock lblDateTo = new TextBlock { Text = "~", VerticalAlignment = VerticalAlignment.Center };
            DatePicker dateTo = new DatePicker { Width = 135, Height = 28, Margin = new Thickness(4, 0, 12, 4) };
            TestResultDateInput.Attach(dateFrom);
            TestResultDateInput.Attach(dateTo);
            Button btnSearch = new Button { Content = "검색", Width = 78, Height = 28, Margin = new Thickness(0, 0, 6, 4) };
            Button btnAll = new Button { Content = "전체조회", Width = 86, Height = 28, Margin = new Thickness(0, 0, 6, 4) };
            Button btnDelete = new Button { Content = "선택삭제", Width = 86, Height = 28,
                Margin = new Thickness(0, 0, 12, 4), Background = System.Windows.Media.Brushes.MistyRose };
            Button btnSave = new Button { Content = "저장", Width = 86, Height = 28,
                Margin = new Thickness(0, 0, 8, 4), Background = System.Windows.Media.Brushes.LightBlue, FontWeight = FontWeights.Bold };
            Button btnExport = new Button { Content = "엑셀출력", Width = 86, Height = 28,
                Margin = new Thickness(0, 0, 8, 4), Background = System.Windows.Media.Brushes.LightGreen, FontWeight = FontWeights.Bold };
            TextBlock lblStatus = new TextBlock { Text = "조회: 0건  │  신규: 0건  │  수정: 0건  │  삭제: 0건",
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                TextAlignment = TextAlignment.Right, Margin = new Thickness(12, 0, 4, 4),
                Foreground = System.Windows.Media.Brushes.DimGray, FontWeight = FontWeights.SemiBold };

            searchBar.Children.Add(lblBatch);
            searchBar.Children.Add(txtBatch);
            searchBar.Children.Add(lblPart);
            searchBar.Children.Add(txtPart);
            searchBar.Children.Add(lblMfDate);
            searchBar.Children.Add(dateFrom);
            searchBar.Children.Add(lblDateTo);
            searchBar.Children.Add(dateTo);
            searchBar.Children.Add(btnSearch);
            searchBar.Children.Add(btnAll);
            Button btnBulk = new Button { Content = "일괄 등록/수정", Width = 118, Height = 28, Margin = new Thickness(0,0,8,4) };
            Button btnArchive = new Button { Content = "Archive 조회", Width = 112, Height = 28, Margin = new Thickness(0,0,8,4) };
            Button btnMoveArchive = new Button { Content = "Archive 이관", Width = 112, Height = 28, Margin = new Thickness(0,0,8,4) };
            actionButtons.Children.Add(btnDelete);
            actionButtons.Children.Add(btnSave);
            actionButtons.Children.Add(btnExport);
            actionButtons.Children.Add(btnBulk);
            var lblArchiveCount = new TextBlock { Text = "(Archive: 조회 전)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4,0,10,4) };
            actionButtons.Children.Add(btnArchive);
            actionButtons.Children.Add(btnMoveArchive);
            DockPanel.SetDock(actionButtons, Dock.Left);
            actionBar.Children.Add(actionButtons);
            var rightStatus = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            rightStatus.Children.Add(lblStatus);
            rightStatus.Children.Add(lblArchiveCount);
            actionBar.Children.Add(rightStatus);
            outerBar.Children.Add(searchBar);
            outerBar.Children.Add(actionBar);
            DockPanel.SetDock(outerBar, Dock.Top);
            mainPanel.Children.Add(outerBar);

            _dataGrid = new DataGrid
            {
                Margin = new Thickness(10, 5, 10, 10),
                IsReadOnly = false,
                CanUserAddRows = false,
                CanUserDeleteRows = true,
                AutoGenerateColumns = true,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                Background = System.Windows.Media.Brushes.White,
                ColumnWidth = new DataGridLength(1, DataGridLengthUnitType.Auto),

                RowHeaderWidth = 22,

                SelectionMode = DataGridSelectionMode.Extended,
                SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
                ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader,
                EnableRowVirtualization = true,
                EnableColumnVirtualization = true
            };

            // Keep recycling enabled even for large result sets.
            VirtualizingPanel.SetIsVirtualizing(_dataGrid, true);
            VirtualizingPanel.SetVirtualizationMode(_dataGrid, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(_dataGrid, true);

            _dataGrid.AutoGeneratingColumn += (s, e) =>
            {
                string colName = e.PropertyName;
                if (colName == "batch no" || colName == "no" || colName == "reg_date" || colName == "upd_date") e.Column.IsReadOnly = true;


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
            // Preserve the existing main-grid clipboard paste workflow.
            _dataGrid.PreviewKeyDown += DataGrid_PreviewKeyDown;
            _dataGrid.CellEditEnding += (sender,e) => {
                if (e.EditAction != DataGridEditAction.Commit) return;
                // Read the edited TextBox value before the DataGrid commits its row.
                // Otherwise the batch propagation can lag until the next Enter.
                if (e.Row.Item is DataRowView rv && e.EditingElement is TextBox editor &&
                    e.Column is DataGridBoundColumn bound && bound.Binding is Binding binding)
                {
                    string field = binding.Path?.Path?.Trim('[', ']') ?? "";
                    if (field == "part" || field == "mfdate")
                    {
                        string batch = Convert.ToString(rv["batch no"]) ?? "";
                        string value = editor.Text;
                        if (field == "mfdate")
                        {
                            if (!TestResultDateInput.TryNormalize(value, out var normalized))
                            {
                                MessageBox.Show("제조일자가 올바르지 않습니다. 예: 250101", "날짜 입력", MessageBoxButton.OK, MessageBoxImage.Warning);
                                e.Cancel = true;
                                return;
                            }
                            value = normalized;
                            editor.Text = value;
                        }
                        Dispatcher.BeginInvoke(new Action(() => {
                            foreach (DataRow r in _dtSource.Rows)
                                if (r.RowState != DataRowState.Deleted &&
                                    string.Equals(Convert.ToString(r["batch no"]), batch, StringComparison.Ordinal) &&
                                    !SameValue(r[field], value, field)) r[field] = value;
                            NormalizeRevertedRows();
                            HighlightChangedCells();
                        }), System.Windows.Threading.DispatcherPriority.Background);
                        return;
                    }
                }
                Dispatcher.BeginInvoke(new Action(() => { NormalizeRevertedRows(); HighlightChangedCells(); }),
                    System.Windows.Threading.DispatcherPriority.Background);
            };
            _dataGrid.LoadingRow += (sender,e) => {
                // A row remains identifiable even when scrolled out of view.
                e.Row.ToolTip = "변경된 셀은 빨간색으로 표시됩니다.";
                Dispatcher.BeginInvoke(new Action(HighlightChangedCells));
            };
            _dataGrid.CurrentCellChanged += (sender,e) => Dispatcher.BeginInvoke(new Action(HighlightChangedCells));

            mainPanel.Children.Add(_dataGrid);

            int archiveCountVersion = 0;
            Action refreshArchiveCount = () => {
                int version = ++archiveCountVersion;
                string batch = txtBatch.Text.Trim(), part = txtPart.Text.Trim();
                DateTime? from = dateFrom.SelectedDate, to = dateTo.SelectedDate;
                lblArchiveCount.Text = "(Archive: 조회 중)";
                _ = System.Threading.Tasks.Task.Run(() => TestResultArchiveStore.CountMatches(batch,part,from,to))
                    .ContinueWith(task => Dispatcher.BeginInvoke(new Action(() => {
                        if (version != archiveCountVersion) return;
                        lblArchiveCount.Text = task.IsFaulted ? "(Archive: 조회 오류)" : $"(Archive: {task.Result.rows:N0}건)";
                    })));
            };
            bool resultTruncated = false;
            int currentRowLimit = 0;

            // DataRowState 기반 실시간 건수 표시 (추가/수정/삭제 모두 포함)
            Action updateStatus = () =>
            {
                int added = 0, modified = 0, deleted = 0, visible = 0;
                foreach (DataRow row in _dtSource.Rows)
                {
                    switch (row.RowState)
                    {
                        case DataRowState.Added: added++; visible++; break;
                        case DataRowState.Modified: if (IsChanged(row)) modified++; visible++; break;
                        case DataRowState.Deleted: deleted++; break;
                        default: visible++; break;
                    }
                }
                lblStatus.Text = $"조회: {visible}건  │  신규: {added}건  │  수정: {modified}건  │  삭제: {deleted}건";
                if (resultTruncated) lblStatus.Text += $"  │  최대 {currentRowLimit}건 표시 (검색 범위를 좁혀주세요)";
            };
            Action<DataTable> watchTable = table =>
            {
                table.RowChanged += (sender, args) => { updateStatus(); Dispatcher.BeginInvoke(new Action(HighlightChangedCells)); };
                table.RowDeleted += (sender, args) => updateStatus();
                table.TableNewRow += (sender, args) => updateStatus();
                updateStatus();
            };
            Func<bool> confirmDiscard = () =>
            {
                // 셀/행 편집 중인 값도 먼저 DataTable에 반영
                _dataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                _dataGrid.CommitEdit(DataGridEditingUnit.Row, true);
                int added = 0, modified = 0, deleted = 0;
                foreach (DataRow row in _dtSource.Rows)
                {
                    if (row.RowState == DataRowState.Added) added++;
                    else if (row.RowState == DataRowState.Modified && IsChanged(row)) modified++;
                    else if (row.RowState == DataRowState.Deleted) deleted++;
                }
                if (added + modified + deleted == 0) return true;
                return MessageBox.Show(
                    $"저장하지 않은 변경 사항이 있습니다.\n신규: {added}건 / 수정: {modified}건 / 삭제: {deleted}건\n계속하면 변경 내용이 사라집니다. 계속하시겠습니까?",
                    "미저장 변경 사항", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            };

            bool skipDiscardOnce = false;
            bool initialLoad = true;
            const int InitialRowLimit = int.MaxValue - 1;
            const int SearchRowLimit = int.MaxValue - 1;

            // 데이터 조회 로직 (네트워크 병목 완벽 방어형으로 튜닝 🎯)
            Action ActionSearch = () =>
            {
                if (!skipDiscardOnce && !confirmDiscard()) return;
                skipDiscardOnce = false;
                DataTable loadedTable = new DataTable();
                string batchKey = txtBatch.Text.Trim();
                string partKey = txtPart.Text.Trim();
                if (dateFrom.SelectedDate.HasValue && dateTo.SelectedDate.HasValue &&
                    dateFrom.SelectedDate.Value.Date > dateTo.SelectedDate.Value.Date)
                {
                    MessageBox.Show("제조일자 시작일은 종료일보다 늦을 수 없습니다.", "검색 조건", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                int rowLimit = initialLoad ? InitialRowLimit : SearchRowLimit;
                initialLoad = false;

                //// 🔒 [보안 및 성능 자물쇠] 네트워크 마비 방지를 위해 검색어가 둘 다 없으면 전체 조회를 막거나 최신순 제한을 둡니다.
                //if (string.IsNullOrEmpty(key1) && string.IsNullOrEmpty(key2))
                //{
                //    MessageBox.Show("네트워크 속도 안정성을 위해 검색어를 입력하신 후 조회를 진행해 주세요.\n(예: batch no, 품번, 검사항목 등)", "검색어 입력 필요", MessageBoxButton.OK, MessageBoxImage.Warning);
                //    return;
                //}

                try
                {
                    ReportLoadStatus("TestResult: DB 연결 중...");
                    using (var conn = new SqliteConnection(DatabaseManager.GetConnectionString("quality")))
                    {
                        conn.Open();
                        ReportLoadStatus("TestResult: SQL 조회 준비 중...");

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
                        sql.AppendLine("    qmir_mf_date AS [mfdate], ");
                        sql.AppendLine("    qmir_reg_date AS [reg_date], ");
                        sql.AppendLine("    qmir_upd_date AS [upd_date] ");
                        sql.AppendLine("FROM qmir_det WHERE 1=1");

                        if (!string.IsNullOrEmpty(batchKey)) sql.AppendLine("AND qmir_batch LIKE @batchLike");
                        if (!string.IsNullOrEmpty(partKey)) sql.AppendLine("AND qmir_part LIKE @partLike");

                        // Stored manufacturing dates include dd/MM/yy; normalize only for date-range comparisons.
                        const string mfDateIso = @"(CASE
                            WHEN qmir_mf_date GLOB '[0-9][0-9]/[0-9][0-9]/[0-9][0-9]'
                                THEN '20' || substr(qmir_mf_date,1,2) || '-' || substr(qmir_mf_date,4,2) || '-' || substr(qmir_mf_date,7,2)
                            WHEN qmir_mf_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]*'
                                THEN substr(qmir_mf_date,1,10)
                            WHEN qmir_mf_date GLOB '[0-9][0-9]/[0-9][0-9]/[0-9][0-9][0-9][0-9]'
                                THEN substr(qmir_mf_date,7,4) || '-' || substr(qmir_mf_date,4,2) || '-' || substr(qmir_mf_date,1,2)
                            ELSE NULL END)";
                        if (dateFrom.SelectedDate.HasValue) sql.AppendLine("AND " + mfDateIso + " >= @dateFrom");
                        if (dateTo.SelectedDate.HasValue) sql.AppendLine("AND " + mfDateIso + " <= @dateTo");

                        // Natural sort for numeric and Q/.../numeric inspection characteristics.
                        // For slash-form codes, the last segment is numeric; numeric-only codes are handled too.
                        const string noNatural = @"CASE
                            WHEN qmir_no <> '' AND qmir_no NOT GLOB '*[^0-9]*' THEN CAST(qmir_no AS INTEGER)
                            WHEN instr(qmir_no, '/') > 0 AND
                                 substr(qmir_no, length(rtrim(qmir_no, '0123456789')) + 1) <> ''
                                 THEN CAST(substr(qmir_no, length(rtrim(qmir_no, '0123456789')) + 1) AS INTEGER)
                            ELSE NULL END";
                        // Group by original prefix first; numeric suffixes are ordered numerically within a prefix.
                        sql.AppendLine(@"ORDER BY COALESCE(NULLIF(qmir_upd_date,''), qmir_reg_date) DESC,
                            qmir_batch ASC,
                            CASE WHEN qmir_no <> '' AND qmir_no NOT GLOB '*[^0-9]*' THEN ''
                                 WHEN instr(qmir_no,'/') > 0 THEN rtrim(qmir_no, '0123456789')
                                 ELSE qmir_no END ASC,");
                        sql.AppendLine(noNatural + " ASC, qmir_no ASC LIMIT @rowLimit");

                        using (var cmd = new SqliteCommand(sql.ToString(), conn))
                        {
                            cmd.Parameters.AddWithValue("@rowLimit", rowLimit + 1);
                            if (!string.IsNullOrEmpty(batchKey)) cmd.Parameters.AddWithValue("@batchLike", "%" + batchKey + "%");
                            if (!string.IsNullOrEmpty(partKey)) cmd.Parameters.AddWithValue("@partLike", "%" + partKey + "%");
                            if (dateFrom.SelectedDate.HasValue) cmd.Parameters.AddWithValue("@dateFrom", dateFrom.SelectedDate.Value.ToString("yyyy-MM-dd"));
                            if (dateTo.SelectedDate.HasValue) cmd.Parameters.AddWithValue("@dateTo", dateTo.SelectedDate.Value.ToString("yyyy-MM-dd"));

                            ReportLoadStatus("TestResult: SQL 실행 중...");
                            using (var reader = cmd.ExecuteReader())
                            {
                                ReportLoadStatus("TestResult: 조회 데이터 적재 중...");
                                loadedTable.Load(reader);
                            }
                        }
                    }

                    bool hasMore = loadedTable.Rows.Count > rowLimit;
                    if (hasMore) loadedTable.Rows.RemoveAt(loadedTable.Rows.Count - 1);
                    resultTruncated = hasMore;
                    currentRowLimit = rowLimit;
                    ReportLoadStatus($"TestResult: DataGrid 표시 중... ({loadedTable.Rows.Count:N0}건)");
                    _dtSource = loadedTable;
                    _dataGrid.AutoGenerateColumns = true;
                    _dataGrid.ItemsSource = null;
                    _dataGrid.ItemsSource = _dtSource.DefaultView;
                    refreshArchiveCount();
                    watchTable(_dtSource);
                    ReportLoadStatus($"TestResult: 조회 완료 ({_dtSource.Rows.Count:N0}건)");

                }
                catch (Exception ex)
                {
                    ReportLoadStatus("TestResult: 조회 실패 - " + ex.Message);
                    MessageBox.Show($"데이터 조회 중 에러 발생:\n{ex.Message}", "조회 오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            // 데이터 일괄 저장 로직
            Action ActionSave = () =>
            {
                _dataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
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

                // Track only explicitly changed batch-wide fields, including edits made by Excel paste.
                var batchChanges = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
                foreach (DataRow row in _dtSource.Rows)
                {
                    if (row.RowState != DataRowState.Modified) continue;
                    string batch = Convert.ToString(row["batch no", DataRowVersion.Original]) ?? "";
                    if (batch.Length == 0) continue;
                    foreach (string field in new[] { "part", "mfdate" })
                    {
                        object before = row[field, DataRowVersion.Original];
                        object after = row[field, DataRowVersion.Current];
                        if (Equals(before, after)) continue;
                        if (!batchChanges.TryGetValue(batch, out var changes))
                            batchChanges[batch] = changes = new Dictionary<string, object>();
                        if (changes.TryGetValue(field, out var existing) && !Equals(existing, after))
                        {
                            MessageBox.Show($"Batch {batch}의 {field} 수정값이 서로 다릅니다. 값을 통일한 후 저장해 주세요.",
                                "Batch 일괄 수정", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                        changes[field] = after;
                    }
                }

                try
                {
                    using (var conn = new SqliteConnection(DatabaseManager.GetConnectionString("quality")))
                    {
                        conn.Open();
                        if (batchChanges.Count > 0)
                        {
                            var info = new StringBuilder();
                            foreach (var entry in batchChanges)
                            {
                                using (var countCmd = new SqliteCommand("SELECT COUNT(*) FROM qmir_det WHERE qmir_batch=@batch", conn))
                                {
                                    countCmd.Parameters.AddWithValue("@batch", entry.Key);
                                    long count = Convert.ToInt64(countCmd.ExecuteScalar());
                                    info.AppendLine($"Batch {entry.Key}: {count}건 ({string.Join(", ", entry.Value.Keys)})");
                                }
                            }
                            if (MessageBox.Show("다음 Batch의 전체 레코드에 수정값을 적용하시겠습니까?\n" + info,
                                "Batch 일괄 수정 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                                return;
                        }
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
                                            qmir_mf_date = @MfDate,
                                            qmir_upd_date = @Today
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
                                        cmd.Parameters.AddWithValue("@Today", DateTime.Today.ToString("yy/MM/dd"));

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
                                            qmir_part, qmir_mf_date, qmir_reg_date, qmir_upd_date
                                        ) VALUES (
                                            @Batch, @No, @Charac, @FirstVal, 
                                            @LastVal, @Ltol, @Utol, @Uom, 
                                            @Part, @MfDate, @Today, @Today
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
                                        cmd.Parameters.AddWithValue("@Today", DateTime.Today.ToString("yy/MM/dd"));
                                        cmd.ExecuteNonQuery();
                                    }
                                    insertCount++;
                                }
                            }
                            foreach (var entry in batchChanges)
                            {
                                var sets = new List<string>();
                                if (entry.Value.ContainsKey("part")) sets.Add("qmir_part = @part");
                                if (entry.Value.ContainsKey("mfdate")) sets.Add("qmir_mf_date = @mfdate");
                                sets.Add("qmir_upd_date = @today");
                                using (var cmd = new SqliteCommand("UPDATE qmir_det SET " + string.Join(", ", sets) + " WHERE qmir_batch = @batch", conn, tx))
                                {
                                    cmd.Parameters.AddWithValue("@batch", entry.Key);
                                    cmd.Parameters.AddWithValue("@today", DateTime.Today.ToString("yy/MM/dd"));
                                    if (entry.Value.TryGetValue("part", out var part)) cmd.Parameters.AddWithValue("@part", part ?? DBNull.Value);
                                    if (entry.Value.TryGetValue("mfdate", out var mfdate)) cmd.Parameters.AddWithValue("@mfdate", mfdate ?? DBNull.Value);
                                    cmd.ExecuteNonQuery();
                                }
                            }
                            tx.Commit();
                        }
                    }

                    if (updateCount > 0 || insertCount > 0 || deleteCount > 0 || _dtSource.Rows.Count > 0)
                    {
                        MessageBox.Show($"실험 결과 저장이 완료되었습니다!\n(수정: {updateCount}건, 추가: {insertCount}건, 삭제: {deleteCount}건)", "저장 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                        skipDiscardOnce = true; // 저장 완료: 기존 DataTable 전체 AcceptChanges 이벤트 폭주 방지
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

                Excel.Application? excelApp = null;
                Excel.Workbook? wb = null;
                Excel.Worksheet? ws = null;

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

            // 메인 윈도우에 연결된 뒤 초기 조회하여 공통 상태표시줄을 사용할 수 있게 합니다.
            System.Windows.RoutedEventHandler initialSearch = null;
            initialSearch = (sender, args) =>
            {
                Loaded -= initialSearch;
                ReportLoadStatus("TestResult: 초기 조회 준비 중...");
                ActionSearch();
            };
            Loaded += initialSearch;

            btnSearch.Click += (s, e) => ActionSearch();
            btnAll.Click += (s, e) =>
            {
                // 기존 검색 조건을 보존할 수 있도록 확인 후에만 초기화
                if (!confirmDiscard()) return;
                txtBatch.Clear();
                txtPart.Clear();
                dateFrom.SelectedDate = null;
                dateTo.SelectedDate = null;
                skipDiscardOnce = true;
                ActionSearch();
            };
            btnSave.Click += (s, e) => ActionSave();
            btnBulk.Click += (s,e) => {
                _dataGrid.CommitEdit(DataGridEditingUnit.Cell,true);
                _dataGrid.CommitEdit(DataGridEditingUnit.Row,true);
                bool dirty = _dtSource.Rows.Cast<DataRow>().Any(r => r.RowState == DataRowState.Deleted || r.RowState == DataRowState.Added || IsChanged(r));
                if (dirty)
                {
                    var answer = MessageBox.Show("저장되지 않은 데이터가 존재합니다. 저장하시겠습니까?\n예: 저장 / 아니요: 저장하지 않음 / 취소", "미저장 데이터",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
                    if (answer == MessageBoxResult.Cancel) return;
                    if (answer == MessageBoxResult.Yes) { ActionSave(); if (_dtSource.Rows.Cast<DataRow>().Any(r => r.RowState == DataRowState.Deleted || r.RowState == DataRowState.Added || IsChanged(r))) return; }
                    else { _dtSource.RejectChanges(); updateStatus(); }
                }
                var win = new TestResultBatchWindow { Owner = Window.GetWindow(this) };
                win.ShowDialog();
                if (win.SavedAnything) { skipDiscardOnce = true; ActionSearch(); }
            };
            btnArchive.Click += (s,e) => {
                if (!confirmDiscard()) return;
                var win = new TestResultArchiveWindow(txtBatch.Text.Trim(),txtPart.Text.Trim(),dateFrom.SelectedDate,dateTo.SelectedDate) { Owner = Window.GetWindow(this) };
                win.ShowDialog(); if (win.Restored) { skipDiscardOnce = true; ActionSearch(); } else refreshArchiveCount();
            };
            btnMoveArchive.Click += (s,e) => {
                if (!confirmDiscard()) return;
                var dlg = new TestResultArchiveMoveWindow { Owner = Window.GetWindow(this) };
                dlg.ShowDialog();
                if (dlg.MovedAnything) { skipDiscardOnce = true; ActionSearch(); }
                else refreshArchiveCount();
            };
            txtBatch.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) ActionSearch(); };
            txtPart.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) ActionSearch(); };

            return mainPanel;
        }

        private void PropagateBatchFields()
        {
            if (_dtSource == null) return;
            var changes = new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
            foreach (DataRow r in _dtSource.Rows)
            {
                if (r.RowState != DataRowState.Modified || !r.HasVersion(DataRowVersion.Original)) continue;
                string batch = Convert.ToString(r["batch no"]) ?? "";
                foreach (string field in new[]{"part","mfdate"})
                {
                    if (SameValue(r[field,DataRowVersion.Original],r[field],field)) continue;
                    if (!changes.TryGetValue(batch,out var fields)) changes[batch]=fields=new Dictionary<string,object>();
                    fields[field]=r[field];
                }
            }
            foreach (DataRow r in _dtSource.Rows)
            {
                if (r.RowState == DataRowState.Deleted) continue;
                if (!changes.TryGetValue(Convert.ToString(r["batch no"]) ?? "",out var fields)) continue;
                foreach (var change in fields) if (!SameValue(r[change.Key],change.Value,change.Key)) r[change.Key]=change.Value;
            }
        }
        private static IEnumerable<T> VisibleDescendants<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            {
                var child=VisualTreeHelper.GetChild(parent,i);
                if (child is T match) yield return match;
                foreach (var nested in VisibleDescendants<T>(child)) yield return nested;
            }
        }
        private void HighlightChangedCells()
        {
            if (_dataGrid == null || _dtSource == null) return;
            // Visit realized cells only; do not iterate hundreds of thousands of virtualized rows.
            foreach (var cell in VisibleDescendants<DataGridCell>(_dataGrid))
            {
                var row = FindParent<DataGridRow>(cell);
                if (row?.Item is not DataRowView rv || cell.Column is not DataGridBoundColumn col || col.Binding is not Binding binding) continue;
                string name = binding.Path?.Path?.Trim('[',']') ?? "";
                if (!rv.Row.Table.Columns.Contains(name)) continue;
                bool changed = rv.Row.RowState == DataRowState.Modified && rv.Row.HasVersion(DataRowVersion.Original)
                    && !SameValue(rv.Row[name,DataRowVersion.Original],rv.Row[name],name);
                if (changed) cell.Background = new SolidColorBrush(Color.FromRgb(255,105,105));
                else cell.ClearValue(DataGridCell.BackgroundProperty);
            }
        }
        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject? current = child;
            while (current != null)
            {
                if (current is T found) return found;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        // 🎯 [핵심 기능] 시작 열(Column) 인덱스 추적 및 메일 HTML/일반 TSV 복사-붙여넣기 완벽 대응
        private void DataGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.V && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
            {
                e.Handled = true; // WPF 순정 붙여넣기 차단

                List<List<string>>? parsedTable = null;

                // 1. 아웃룩/웹메일 HTML 표 우선 파싱
                if (Clipboard.ContainsData(DataFormats.Html))
                {
                    string? htmlText = Clipboard.GetData(DataFormats.Html) as string;
                    if (!string.IsNullOrEmpty(htmlText))
                    {
                        parsedTable = ParseHtmlTable(htmlText);
                    }
                }

                // 2. HTML 실패 시 일반 텍스트(TSV) 파싱
                if (parsedTable == null || parsedTable.Count == 0)
                {
                    string clipboardText = Clipboard.GetText();
                    if (string.IsNullOrWhiteSpace(clipboardText)) return;
                    parsedTable = ParseTsvWithQuotes(clipboardText);
                }

                if (parsedTable == null || parsedTable.Count == 0) return;

                // Paste into existing visible rows and editable columns only.
                _dataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                _dataGrid.CommitEdit(DataGridEditingUnit.Row, true);
                int startRowIndex = _dataGrid.CurrentItem == null ? _dataGrid.SelectedIndex : _dataGrid.Items.IndexOf(_dataGrid.CurrentItem);
                if (startRowIndex < 0) return;
                int startColumnIndex = _dataGrid.CurrentCell.IsValid && _dataGrid.CurrentCell.Column != null
                    ? _dataGrid.CurrentCell.Column.DisplayIndex : 0;
                var visibleRows = _dataGrid.Items.OfType<DataRowView>().ToList();
                var orderedColumns = _dataGrid.Columns.OrderBy(c => c.DisplayIndex).ToList();
                try
                {
                    for (int i = 0; i < parsedTable.Count && startRowIndex + i < visibleRows.Count; i++)
                    {
                        var targetRow = visibleRows[startRowIndex + i].Row;
                        if (targetRow.RowState == DataRowState.Deleted) continue;
                        for (int j = 0; j < parsedTable[i].Count && startColumnIndex + j < orderedColumns.Count; j++)
                        {
                            var col = orderedColumns[startColumnIndex + j];
                            if (col.IsReadOnly || col is not DataGridBoundColumn bound || bound.Binding is not Binding binding) continue;
                            string name = binding.Path?.Path?.Trim('[', ']') ?? "";
                            if (!_dtSource.Columns.Contains(name) || name == "batch no" || name == "no") continue;
                            string value = parsedTable[i][j].Trim();
                            targetRow[name] = value;
                        }
                    }
                    NormalizeRevertedRows();
                    HighlightChangedCells();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("붙여넣기 오류: " + ex.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

            }
        }

        /// <summary>
        /// 🎯 메일/웹 HTML 표 구조(tr, td)를 직접 추출하는 파서
        /// </summary>
        private List<List<string>> ParseHtmlTable(string html)
        {
            var table = new List<List<string>>();

            try
            {
                var trMatches = System.Text.RegularExpressions.Regex.Matches(
                    html, @"<tr[^>]*>(.*?)</tr>", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                foreach (System.Text.RegularExpressions.Match trMatch in trMatches)
                {
                    var row = new List<string>();
                    var tdMatches = System.Text.RegularExpressions.Regex.Matches(
                        trMatch.Groups[1].Value, @"<t[dh][^>]*>(.*?)</t[dh]>", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    foreach (System.Text.RegularExpressions.Match tdMatch in tdMatches)
                    {
                        string cellHtml = tdMatch.Groups[1].Value;
                        string cellText = System.Text.RegularExpressions.Regex.Replace(cellHtml, @"<[^>]+>", "").Trim();
                        cellText = System.Net.WebUtility.HtmlDecode(cellText);
                        cellText = System.Text.RegularExpressions.Regex.Replace(cellText, @"\s+", " ").Trim();
                        row.Add(cellText);
                    }

                    if (row.Count > 0 && row.Exists(s => !string.IsNullOrEmpty(s)))
                    {
                        table.Add(row);
                    }
                }
            }
            catch { }

            return table;
        }

        /// <summary>
        /// 🎯 일반 TSV(텍스트) 파서 (쌍따옴표 내 줄바꿈 보존)
        /// </summary>
        private List<List<string>> ParseTsvWithQuotes(string text)
        {
            var result = new List<List<string>>();
            var currentRow = new List<string>();
            var currentCell = new System.Text.StringBuilder();
            bool inQuotes = false;

            string normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");

            for (int i = 0; i < normalized.Length; i++)
            {
                char c = normalized[i];

                if (c == '"')
                {
                    if (inQuotes && i + 1 < normalized.Length && normalized[i + 1] == '"')
                    {
                        currentCell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == '\t' && !inQuotes)
                {
                    currentRow.Add(currentCell.ToString().Trim());
                    currentCell.Clear();
                }
                else if (c == '\n' && !inQuotes)
                {
                    currentRow.Add(currentCell.ToString().Trim());
                    currentCell.Clear();

                    if (currentRow.Exists(s => !string.IsNullOrWhiteSpace(s)))
                    {
                        result.Add(currentRow);
                    }
                    currentRow = new List<string>();
                }
                else
                {
                    currentCell.Append(c);
                }
            }

            if (currentCell.Length > 0 || currentRow.Count > 0)
            {
                currentRow.Add(currentCell.ToString().Trim());
                if (currentRow.Exists(s => !string.IsNullOrWhiteSpace(s)))
                {
                    result.Add(currentRow);
                }
            }

            return result;
        }
    }
}