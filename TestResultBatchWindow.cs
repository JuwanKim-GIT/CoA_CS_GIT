using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;
using Orientation = System.Windows.Controls.Orientation;
using Window = System.Windows.Window;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using InputKey = System.Windows.Input.Key;

namespace QCS
{
    // Separate, self-contained bulk entry window. Never modifies the main grid's pending edits.
    internal sealed class TestResultBatchWindow : Window
    {
        private static readonly string[] Names = { "batch no", "no", "Characteristic", "First Value", "Last Value", "Lower Limit", "Upper Limit", "UoM", "part", "mfdate" };
        private static readonly string[] DbNames = { "qmir_batch", "qmir_no", "qmir_charac", "qmir_first_value", "qmir_last_value", "qmir_ltol", "qmir_utol", "qmir_uom", "qmir_part", "qmir_mf_date" };
        private readonly DataTable pending = CreateTable(true);
        private readonly DataTable results = CreateTable(true);
        private readonly DataGrid inputGrid = new DataGrid();
        private readonly DataGrid resultGrid = new DataGrid();
        private readonly TextBlock summary = new TextBlock();
        private readonly System.Windows.Controls.CheckBox overwriteMf = new System.Windows.Controls.CheckBox { Content = "mf date 덮어쓰기", IsChecked = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12,0,12,0) };
        private readonly TextBlock inputCount = new TextBlock();
        private int inserted, updated, unchanged;
        public bool SavedAnything { get; private set; }
        private static DataTable CreateTable(bool includeStatus)
        {
            var t = new DataTable();
            foreach (var n in Names) t.Columns.Add(n, typeof(string));
            if (includeStatus) { t.Columns.Add("처리", typeof(string)); t.Columns.Add("오류 사유", typeof(string)); }
            return t;
        }
        public TestResultBatchWindow()
        {
            Title = "Test Result 일괄 등록 / 수정";
            Width = 1360; Height = 850; MinWidth = 900; MinHeight = 580;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new DockPanel { Margin = new Thickness(12) };
            Content = root;
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var close = new Button { Content = "닫기", Width = 90, Height = 32 };
            close.Click += (s,e) => Close(); footer.Children.Add(close);
            DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(9) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(grid);
            var upper = new DockPanel(); Grid.SetRow(upper,0); grid.Children.Add(upper);
            var upperBar = new DockPanel { Margin = new Thickness(0,0,0,6) };
            DockPanel.SetDock(upperBar,Dock.Top); upper.Children.Add(upperBar);
            var save = new Button { Content = "DB 저장", Width = 100, Height = 32, FontWeight = FontWeights.SemiBold, Background = new SolidColorBrush(Color.FromRgb(45,101,204)), Foreground = Brushes.White };
            save.Click += (s,e) => SavePending(); DockPanel.SetDock(save,Dock.Right); upperBar.Children.Add(save);
            DockPanel.SetDock(overwriteMf,Dock.Right); upperBar.Children.Add(overwriteMf);
            var label = new StackPanel { Orientation = Orientation.Vertical };
            label.Children.Add(new TextBlock { Text = "상단 · 저장 전 데이터", FontSize = 15, FontWeight = FontWeights.SemiBold });
            label.Children.Add(inputCount);
            upperBar.Children.Add(label);
            SetupGrid(inputGrid, pending, true); upper.Children.Add(inputGrid);
            var separator = new Border { Background = Brushes.LightGray, Height = 1, Margin = new Thickness(0,4,0,4) }; Grid.SetRow(separator,1); grid.Children.Add(separator);
            var lower = new DockPanel(); Grid.SetRow(lower,2); grid.Children.Add(lower);
            summary.FontWeight = FontWeights.SemiBold; summary.FontSize = 15; summary.Margin = new Thickness(0,0,0,7);
            DockPanel.SetDock(summary,Dock.Top); lower.Children.Add(summary);
            SetupGrid(resultGrid,results,false); lower.Children.Add(resultGrid);
            UpdateSummary();
            PreviewKeyDown += (s,e) => { if (e.Key == InputKey.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; Paste(); } };
        }
        private static void SetupGrid(DataGrid g, DataTable table, bool upper)
        {
            g.ItemsSource = table.DefaultView; g.IsReadOnly = true; g.CanUserAddRows = false;
            g.AutoGenerateColumns = true; g.EnableRowVirtualization = true; g.EnableColumnVirtualization = true;
            g.GridLinesVisibility = DataGridGridLinesVisibility.All;
            g.AutoGeneratingColumn += (s,e) => {
                if (e.PropertyName == "처리" && upper) { e.Cancel = true; return; }
                if (e.PropertyName == "오류 사유" && !upper) { e.Cancel = true; return; }
                if (e.PropertyName == "Characteristic") e.Column.Width = new DataGridLength(260);
                else if (e.PropertyName == "오류 사유") e.Column.Width = new DataGridLength(240);
                else e.Column.Width = new DataGridLength(115);
            };
            if (upper) g.LoadingRow += (s,e) => {
                e.Row.ClearValue(DataGridRow.BackgroundProperty);
                e.Row.Dispatcher.BeginInvoke(new Action(() => {
                    if (e.Row.Item is not DataRowView rv) return;
                    string error = Convert.ToString(rv["오류 사유"]) ?? "";
                    foreach (var column in g.Columns)
                    {
                        if (column is not DataGridBoundColumn bound || bound.Binding is not System.Windows.Data.Binding binding) continue;
                        string name = binding.Path?.Path?.Trim('[',']') ?? "";
                        var content = column.GetCellContent(rv);
                        if (content == null) continue;
                        var cell = content.Parent as DataGridCell;
                        if (cell == null) continue;
                        bool isError = error.Contains(name, StringComparison.OrdinalIgnoreCase)
                            || (error.Contains("필수") && (name=="batch no" || name=="no") && string.IsNullOrWhiteSpace(Convert.ToString(rv[name])))
                            || (name=="오류 사유" && error.Length>0);
                        if (isError) cell.Background = new SolidColorBrush(Color.FromRgb(255,105,105));
                        else cell.ClearValue(DataGridCell.BackgroundProperty);
                    }
                }));
            };
        }
        private void UpdateSummary()
        {
            inputCount.Text = $"Ctrl+V: 선택 셀과 무관하게 전체 교체 · 입력 {pending.Rows.Count}건 · 셀 직접 수정 불가";
            summary.Text = $"하단 · 저장 완료 누적 결과    신규 {inserted}건  |  수정 {updated}건  |  변경 없음 {unchanged}건";
        }
        private static List<string[]> ParseClipboard()
        {
            var text = Clipboard.GetText();
            var rows = new List<string[]>();
            if (string.IsNullOrWhiteSpace(text)) return rows;
            // TSV from Excel; preserve empty cells, trailing blank line and quoted tabs/newlines.
            var cells = new List<string>(); var cell = new StringBuilder(); bool quoted = false;
            string normalized = text.Replace("\r\n", "\n").Replace('\r','\n');
            foreach (char ch in normalized)
            {
                if (ch == '"') { quoted = !quoted; continue; }
                if (ch == '\t' && !quoted) { cells.Add(cell.ToString()); cell.Clear(); }
                else if (ch == '\n' && !quoted) {
                    cells.Add(cell.ToString()); cell.Clear();
                    if (cells.Any(x => !string.IsNullOrWhiteSpace(x))) rows.Add(cells.ToArray());
                    cells = new List<string>();
                } else cell.Append(ch);
            }
            if (cell.Length > 0 || cells.Count > 0) { cells.Add(cell.ToString()); if (cells.Any(x=>!string.IsNullOrWhiteSpace(x))) rows.Add(cells.ToArray()); }
            return rows;
        }
        private void Paste()
        {
            try
            {
                var rows = ParseClipboard(); if (rows.Count == 0) return;
                pending.Clear();
                foreach (var cells in rows)
                {
                    var r = pending.NewRow();
                    for (int i=0;i<Math.Min(cells.Length,Names.Length);i++) r[i] = cells[i].Trim();
                    if (TestResultDateInput.TryNormalize(Convert.ToString(r["mfdate"]), out var dateValue))
                        r["mfdate"] = dateValue;
                    if (cells.Length != Names.Length) r["오류 사유"] = $"열 개수 오류 ({cells.Length}/{Names.Length})";
                    pending.Rows.Add(r);
                }
                UpdateSummary(); inputGrid.Items.Refresh();
            }
            catch (Exception ex) { MessageBox.Show("붙여넣기 오류: " + ex.Message); }
        }
        private static string NormalizePart(string? value)
        {
            string s = (value ?? "").Trim();
            if (s.Length > 0 && s.All(char.IsDigit)) return s.TrimStart('0').PadLeft(1,'0');
            return s;
        }
        private static bool EqualValue(string name, object? before, string? after)
        {
            if (name == "part") return NormalizePart(Convert.ToString(before)) == NormalizePart(after);
            if (new[] { "First Value", "Last Value", "Lower Limit", "Upper Limit" }.Contains(name))
            {
                return double.TryParse(Convert.ToString(before), NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                    && double.TryParse(after, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && a == b;
            }
            return string.Equals(Convert.ToString(before) ?? "", after ?? "", StringComparison.Ordinal);
        }
        private static bool TryNumber(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }
        private static double ParseNumber(string s)
        {
            if (TryNumber(s,out var d)) return d;
            throw new FormatException("숫자 형식 오류: " + s);
        }
        private static string Key(DataRow row) => Convert.ToString(row["batch no"]) + "\u001f" + Convert.ToString(row["no"]);
        private static bool ExistsInDatabase(SqliteConnection conn, DataRow row)
        {
            using var cmd = new SqliteCommand("SELECT EXISTS(SELECT 1 FROM qmir_det WHERE qmir_batch=@batch AND qmir_no=@no)", conn);
            cmd.Parameters.AddWithValue("@batch", row["batch no"]);
            cmd.Parameters.AddWithValue("@no", row["no"]);
            return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
        }
        private void SavePending()
        {
            if (pending.Rows.Count == 0) return;
            var latest = new Dictionary<string,DataRow>(StringComparer.Ordinal);
            foreach (DataRow r in pending.Rows) latest[Key(r)] = r;
            var candidates = latest.Values.ToList();
            var groups = candidates.GroupBy(r => Convert.ToString(r["batch no"]) ?? "",StringComparer.Ordinal).ToList();
            var completed = new List<(DataRow row,string status)>();
            try
            {
                using (var conn = new SqliteConnection(DatabaseManager.GetConnectionString("quality")))
                {
                    conn.Open();
                    foreach (var group in groups)
                    {
                        var errors = new Dictionary<DataRow,string>();
                        foreach (var r in group)
                        {
                            string error = Convert.ToString(r["오류 사유"]) ?? "";
                            if (string.IsNullOrWhiteSpace(Convert.ToString(r["batch no"]))) error = "Batch No 필수";
                            else if (string.IsNullOrWhiteSpace(Convert.ToString(r["no"]))) error = "No 필수";
                            else foreach (int i in new[]{3,4,5,6})
                                if (!string.IsNullOrWhiteSpace(Convert.ToString(r[i])) && !TryNumber(Convert.ToString(r[i]) ?? "",out _)) { error = Names[i] + " 숫자 오류"; break; }
                            if (error.Length == 0 && (overwriteMf.IsChecked == true || !ExistsInDatabase(conn, r)) &&
                                !TestResultDateInput.TryNormalize(Convert.ToString(r["mfdate"]), out _))
                                error = "mfdate 날짜 오류";
                            if (error.Length > 0) errors[r] = error;
                        }
                        // Batch-wide fields must be consistent within each pasted Batch.
                        foreach (string name in new[]{"part","mfdate"})
                        {
                            if (name == "mfdate" && overwriteMf.IsChecked != true) continue;
                            var vals = group.Select(r=>name == "part" ? NormalizePart(Convert.ToString(r[name])) : Convert.ToString(r[name]) ?? "").Distinct().ToList();
                            if (vals.Count > 1) foreach (var r in group) errors[r] = name + " Batch 내 값 불일치";
                        }
                        if (errors.Count > 0)
                        {
                            foreach (var r in group) r["오류 사유"] = errors.TryGetValue(r,out var msg) ? msg : "동일 Batch 오류로 저장 보류";
                            continue;
                        }
                        using (var tx = conn.BeginTransaction())
                        {
                            try
                            {
                                var groupDone = new List<(DataRow row,string status)>();
                                foreach (var r in group)
                                {
                                    using var check = new SqliteCommand("SELECT qmir_charac,qmir_first_value,qmir_last_value,qmir_ltol,qmir_utol,qmir_uom,qmir_part,qmir_mf_date FROM qmir_det WHERE qmir_batch=@b AND qmir_no=@n",conn,tx);
                                    check.Parameters.AddWithValue("@b",r["batch no"]); check.Parameters.AddWithValue("@n",r["no"]);
                                    bool exists; bool changed = false;
                                    using (var reader = check.ExecuteReader())
                                    {
                                        exists = reader.Read();
                                        if (exists) for (int i=2;i<Names.Length;i++)
                                        {
                                            var before = reader.GetValue(i-2);
                                            if (i==9 && overwriteMf.IsChecked!=true) continue;
                                            if (!EqualValue(Names[i],before,Convert.ToString(r[i]))) { changed=true; break; }
                                        }
                                    }
                                    string status = !exists ? "신규" : changed ? "수정" : "변경 없음";
                                    if (true) // Explicit DB save always refreshes upd_date, even without other changes.
                                    {
                                        var sql = exists
                                            ? "UPDATE qmir_det SET " + string.Join(",",DbNames.Skip(2).Where(n=>overwriteMf.IsChecked==true || n!="qmir_mf_date").Select(n=>n+"=@v"+Array.IndexOf(DbNames,n))) + ",qmir_upd_date=@today WHERE qmir_batch=@v0 AND qmir_no=@v1"
                                            : "INSERT INTO qmir_det ("+string.Join(",",DbNames)+",qmir_reg_date,qmir_upd_date) VALUES ("+string.Join(",",Enumerable.Range(0,10).Select(i=>"@v"+i))+",@today,@today)";
                                        using var cmd = new SqliteCommand(sql,conn,tx);
                                        cmd.Parameters.AddWithValue("@today", DateTime.Today.ToString("yy/MM/dd"));
                                        for (int i=0;i<10;i++)
                                        {
                                            object val = r[i];
                                            if (i>=3 && i<=6) val = string.IsNullOrWhiteSpace(Convert.ToString(r[i])) ? DBNull.Value : (object)ParseNumber(Convert.ToString(r[i])!);
                                            if (i==8 && exists)
                                            {
                                                // Preserve existing zero padding for semantically equal part numbers.
                                                using var oldPart = new SqliteCommand("SELECT qmir_part FROM qmir_det WHERE qmir_batch=@b AND qmir_no=@n",conn,tx);
                                                oldPart.Parameters.AddWithValue("@b",r[0]); oldPart.Parameters.AddWithValue("@n",r[1]);
                                                var existingPart = Convert.ToString(oldPart.ExecuteScalar());
                                                if (NormalizePart(existingPart) == NormalizePart(Convert.ToString(val))) val = existingPart ?? "";
                                            }
                                            cmd.Parameters.AddWithValue("@v"+i,val);
                                        }
                                        cmd.ExecuteNonQuery();
                                    }
                                    groupDone.Add((r,status));
                                }
                                tx.Commit(); completed.AddRange(groupDone);
                            }
                            catch (Exception ex)
                            {
                                tx.Rollback(); foreach (var r in group) r["오류 사유"] = "DB 오류: " + ex.Message;
                            }
                        }
                    }
                }
                foreach (var item in completed)
                {
                    var dest = results.NewRow();
                    for (int i=0;i<Names.Length;i++) dest[i] = item.row[i];
                    dest["처리"] = item.status; results.Rows.Add(dest);
                    if (item.status == "신규") inserted++;
                    else if (item.status == "수정") updated++;
                    else unchanged++;
                }
                if (completed.Count > 0) SavedAnything = true;
                // Keep only failed batches in the upper grid; successful rows are already committed.
                var successfulKeys = new HashSet<string>(completed.Select(c=>Key(c.row)),StringComparer.Ordinal);
                var keep = pending.Rows.Cast<DataRow>().Where(r => !successfulKeys.Contains(Key(r))).ToList();
                var copies = keep.Select(r=>r.ItemArray.ToArray()).ToList(); pending.Clear();
                foreach (var a in copies) pending.Rows.Add(a);
                UpdateSummary(); inputGrid.Items.Refresh();
            }
            catch (Exception ex) { MessageBox.Show("일괄 저장 실패: " + ex.Message,"DB 오류",MessageBoxButton.OK,MessageBoxImage.Error); }
        }
    }
}
