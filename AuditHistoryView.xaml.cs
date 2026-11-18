using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Data.Sqlite;
using UserControl = System.Windows.Controls.UserControl;
using MessageBox = System.Windows.MessageBox;

namespace QCS
{
    public partial class AuditHistoryView : UserControl
    {
        private sealed record Revision(long Number, string Label);
        private sealed record AuditItem(string GracoCode, string When, string User, string Action, string Product, long Rev, string Content);
        private sealed record ChangeItem(string Table, string Field, string Old, string New);
        private readonly List<AuditItem> _all = new();
        private bool _loading;

        public AuditHistoryView()
        {
            InitializeComponent();
            Loaded += (_, _) => { if (RevisionCombo.Items.Count == 0) LoadRevisions(); };
        }

        private static string ColumnName(HashSet<string> columns, params string[] candidates) =>
            candidates.FirstOrDefault(columns.Contains) ?? string.Empty;

        private static HashSet<string> Columns(SqliteConnection db, string table)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(reader.GetString(1));
            return result;
        }

        private static string SqlText(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? "" : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? "";

        private void LoadRevisions()
        {
            try
            {
                _loading = true;
                long pending;
                using (var common = new SqliteConnection(DatabaseManager.GetConnectionString("common")))
                {
                    common.Open();
                    using var cmd = common.CreateCommand();
                    cmd.CommandText = "SELECT zx_code_num1 FROM zx_code_mstr WHERE zx_code_fldname=$field AND (zx_code_value='' OR zx_code_value IS NULL)";
                    cmd.Parameters.AddWithValue("$field", "MSDS_REV_NEXT_NUMBER");
                    var numbers = new List<long>();
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) if (!reader.IsDBNull(0)) numbers.Add(Convert.ToInt64(reader.GetValue(0)));
                    if (numbers.Count != 1 || numbers[0] <= 0) throw new InvalidOperationException("현재 진행중인 대장 REV 설정이 유효하지 않습니다.");
                    pending = numbers[0];
                }
                var revisions = new List<Revision> { new(pending, $"{pending:D4} (진행중)") };
                using (var db = new SqliteConnection(DatabaseManager.GetConnectionString("msds")))
                {
                    db.Open();
                    var cols = Columns(db, "rev_mstr");
                    var noCol = ColumnName(cols, "rev_no", "rev_mstr_no", "rev_number", "rev_mstr_rev_no", "rev_mstr_number");
                    var dateCol = ColumnName(cols, "rev_date", "rev_mstr_date", "rev_revision_date", "rev_mstr_revision_date", "rev_confirmed_at", "rev_mstr_created_at", "rev_created_at", "rev_reg_date", "rev_mstr_reg_date", "rev_datetime", "rev_mstr_datetime");
                    if (string.IsNullOrEmpty(dateCol)) dateCol = cols.FirstOrDefault(c => c.Contains("date", StringComparison.OrdinalIgnoreCase) || c.Contains("일자", StringComparison.OrdinalIgnoreCase)) ?? "";
                    if (string.IsNullOrEmpty(noCol) || string.IsNullOrEmpty(dateCol))
                        throw new InvalidOperationException("rev_mstr의 개정번호/개정일자 컬럼명을 확인할 수 없습니다. 실제 스키마 확인이 필요합니다.");
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = $"SELECT \"{noCol}\", \"{dateCol}\" FROM rev_mstr WHERE \"{noCol}\" IS NOT NULL ORDER BY CAST(\"{noCol}\" AS INTEGER) DESC";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        if (!long.TryParse(SqlText(reader, 0), out long number) || number == pending) continue;
                        string date = SqlText(reader, 1);
                        if (DateTime.TryParse(date, out var parsed)) date = parsed.ToString("yyyy-MM-dd");
                        revisions.Add(new Revision(number, $"{number:D4} ({date})"));
                    }
                }
                RevisionCombo.ItemsSource = revisions;
                var ordered = revisions.OrderBy(r => r.Number).ToList();
                FromRevCombo.ItemsSource = ordered;
                ToRevCombo.ItemsSource = ordered;
                if (ordered.Count > 0) { FromRevCombo.SelectedIndex = 0; ToRevCombo.SelectedIndex = ordered.Count - 1; }
                RevisionCombo.SelectedIndex = 0;
            }
            catch (Exception ex) { ShowError("대장 REV 목록 조회 실패", ex); }
            finally { _loading = false; }
            LoadHistory();
        }

        private void RevisionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) LoadHistory(); }
        private void SearchClick(object sender, RoutedEventArgs e) => ApplyFilter();
        private void SearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Enter) { ApplyFilter(); e.Handled = true; } }

        private void LoadHistory()
        {
            if (_loading || RevisionCombo.SelectedItem is not Revision selected) return;
            try
            {
                _all.Clear();
                using var db = new SqliteConnection(DatabaseManager.GetConnectionString("msds"));
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"SELECT audit_log_graco_code, audit_log_datetime, audit_log_user_id,
                        audit_log_action, audit_log_product_name, audit_log_rev_no, audit_log_content
                    FROM audit_log_hist WHERE audit_log_rev_no=$rev
                    ORDER BY audit_log_graco_code COLLATE NOCASE, audit_log_datetime DESC, audit_log_user_id COLLATE NOCASE";
                cmd.Parameters.AddWithValue("$rev", selected.Number);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    _all.Add(new AuditItem(SqlText(reader, 0), SqlText(reader, 1), SqlText(reader, 2),
                        SqlText(reader, 3), SqlText(reader, 4), selected.Number, SqlText(reader, 6)));
                ApplyFilter();
            }
            catch (Exception ex) { ShowError("Audit 이력 조회 실패", ex); }
        }

        private void ApplyFilter()
        {
            string term = SearchBox.Text.Trim();
            var filtered = _all.Where(x => term.Length == 0 ||
                new[] { x.GracoCode, x.When, x.User, x.Action, x.Product, x.Content, x.Rev.ToString() }
                    .Any(s => s.Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();
            HistoryGrid.ItemsSource = filtered;
            CountText.Text = $"조회 결과: {filtered.Count:N0}건";
            HistoryGrid.SelectedIndex = filtered.Count > 0 ? 0 : -1;
            if (filtered.Count == 0) ClearDetail();
        }

        private static string JsonValue(JsonElement element)
        {
            if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "";
            return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString();
        }
        private static string Property(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? JsonValue(value) : "";
        private void HistorySelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HistoryGrid.SelectedItem is not AuditItem item) { ClearDetail(); return; }
            DetailGraco.Text = item.GracoCode;
            DetailRev.Text = item.Rev.ToString("D4");
            DetailDate.Text = item.When;
            DetailUser.Text = item.User;
            DetailAction.Text = item.Action;
            DetailProduct.Text = item.Product;
            var changes = new List<ChangeItem>();
            try
            {
                using var doc = JsonDocument.Parse(item.Content);
                if (doc.RootElement.TryGetProperty("changes", out var entries) && entries.ValueKind == JsonValueKind.Array)
                    foreach (var entry in entries.EnumerateArray())
                        changes.Add(new ChangeItem(Property(entry, "table"), Property(entry, "field"), Property(entry, "old"), Property(entry, "new")));
            }
            catch (JsonException) { changes.Add(new ChangeItem("", "원본 이력", "", item.Content)); }
            ChangesGrid.ItemsSource = changes;
        }
        private void ClearDetail()
        {
            DetailGraco.Text = DetailRev.Text = DetailDate.Text = DetailUser.Text = DetailAction.Text = DetailProduct.Text = "";
            ChangesGrid.ItemsSource = null;
        }
        private void OpenOriginalClick(object sender, RoutedEventArgs e)
        {
            if (HistoryGrid.SelectedItem is not AuditItem item) { SetStatus("MSDS 이력을 먼저 선택해 주세요."); return; }
            try
            {
                using var db = new SqliteConnection(DatabaseManager.GetConnectionString("msds"));
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT msds_file_path FROM msds_mstr WHERE msds_graco_code=$code AND COALESCE(msds_is_deleted,0)=0 ORDER BY msds_msds_id DESC";
                cmd.Parameters.AddWithValue("$code", item.GracoCode);
                using var reader = cmd.ExecuteReader();
                var paths = new List<string>();
                while (reader.Read()) if (!reader.IsDBNull(0)) paths.Add(reader.GetString(0));
                if (paths.Count != 1) { SetStatus("해당 이력에 연결된 PDF를 단일 파일로 특정할 수 없습니다. MSDS 관리 화면에서 확인해 주세요."); return; }
                if (!File.Exists(paths[0])) { SetStatus("PDF 파일을 찾을 수 없습니다: " + paths[0]); return; }
                Process.Start(new ProcessStartInfo(paths[0]) { UseShellExecute = true });
                SetStatus("MSDS 원문을 열었습니다.");
            }
            catch (Exception ex) { ShowError("MSDS 원문 열기 실패", ex); }
        }

        private sealed record InventoryLine(string[] Cells);

        private static string ReadField(SqliteDataReader reader, IReadOnlyDictionary<string, int> positions, string column)
        {
            if (!positions.TryGetValue(column, out int index) || reader.IsDBNull(index)) return "";
            return Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? "";
        }

        private static List<InventoryLine> ReadInventory(string materialType, bool latestOnly)
        {
            var result = new List<InventoryLine>();
            using var db = new SqliteConnection(DatabaseManager.GetConnectionString("msds"));
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT m.*, c.msds_comp_name, c.msds_comp_cas_no,
                    c.msds_comp_content_raw, c.msds_comp_content_max,
                    c.msds_comp_molecular_formula, c.msds_comp_twa, c.msds_comp_stel
                FROM msds_mstr m LEFT JOIN msds_comp_dtl c ON c.msds_comp_parent_msds_id=m.msds_msds_id
                WHERE m.msds_material_type=$type AND COALESCE(m.msds_is_deleted,0)=0
                AND ($latest=0 OR UPPER(TRIM(CAST(m.msds_is_latest AS TEXT))) IN ('1','Y','YES'))
                ORDER BY m.msds_graco_code COLLATE NOCASE, m.msds_msds_id DESC,
                         c.msds_comp_sort_order, c.msds_comp_msds_id";
            cmd.Parameters.AddWithValue("$type", materialType);
            cmd.Parameters.AddWithValue("$latest", latestOnly ? 1 : 0);
            using var reader = cmd.ExecuteReader();
            var pos = Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => i, StringComparer.OrdinalIgnoreCase);
            long lastId = -1;
            while (reader.Read())
            {
                string V(string key) => ReadField(reader, pos, key);
                bool first = long.TryParse(V("msds_msds_id"), out long id) && id != lastId;
                if (first) lastId = id;
                var cells = new string[39]; // Excel A:AM (1-based columns 1..39)
                cells[0] = V("msds_graco_code");
                if (first)
                {
                    cells[1] = V("msds_product_name"); cells[2] = V("msds_supplier_name");
                    cells[3] = V("msds_supplier_phone"); cells[4] = V("msds_supplier_address");
                    cells[5] = V("msds_revision_date"); cells[6] = V("msds_revision_no");
                    cells[7] = V("msds_h_codes"); cells[8] = V("msds_corrosive_flag");
                    cells[16] = V("msds_lel"); cells[17] = V("msds_uel");
                    cells[18] = V("msds_flash_point"); cells[19] = V("msds_autoignition_temp");
                    cells[20] = V("msds_vapor_pressure"); cells[21] = V("msds_reactivity");
                    cells[25] = V("msds_un_number"); cells[38] = V("msds_moel_submission_no");
                }
                cells[9] = V("msds_comp_name"); cells[10] = V("msds_comp_cas_no");
                cells[11] = V("msds_comp_content_raw"); cells[12] = V("msds_comp_content_max");
                cells[13] = V("msds_comp_molecular_formula");
                cells[14] = V("msds_comp_twa"); cells[15] = V("msds_comp_stel");
                result.Add(new InventoryLine(cells));
            }
            return result;
        }

        private static string TemplatePath(string materialType)
        {
            string name = materialType == "RM" ? "원료 유해위험물질목록.xltm" : "제품 유해위험물질목록.xltm";
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "QCS_xltm", name);
            if (!File.Exists(path)) throw new FileNotFoundException("Excel 템플릿이 실행 폴더의 QCS_xltm에 없습니다.", path);
            return path;
        }

        private static void OpenInventoryWorkbooks(List<InventoryLine> rm, List<InventoryLine> fg, long revision)
        {
            string rmPath = TemplatePath("RM"), fgPath = TemplatePath("FG");
            var excelType = Type.GetTypeFromProgID("Excel.Application")
                ?? throw new InvalidOperationException("Microsoft Excel이 설치되어 있지 않습니다.");
            dynamic excel = Activator.CreateInstance(excelType)
                ?? throw new InvalidOperationException("Microsoft Excel을 실행할 수 없습니다.");
            dynamic? rmBook = null, fgBook = null;
            try
            {
                excel.Visible = false;
                excel.DisplayAlerts = false;
                // Disable macro execution while instantiating workbook templates.
                excel.AutomationSecurity = 3;
                rmBook = excel.Workbooks.Add(rmPath);
                fgBook = excel.Workbooks.Add(fgPath);
                FillInventorySheet(rmBook, rm, revision);
                FillInventorySheet(fgBook, fg, revision);
                excel.DisplayAlerts = true;
                excel.Visible = true;
                rmBook.Worksheets[1].Activate();
            }
            catch
            {
                try { if (fgBook != null) fgBook.Close(false); } catch { }
                try { if (rmBook != null) rmBook.Close(false); } catch { }
                try { excel.Quit(); } catch { }
                throw;
            }
        }

        private static void FillInventorySheet(dynamic book, List<InventoryLine> lines, long revision)
        {
            dynamic sheet = book.Worksheets[1];
            int last = Math.Max(9, Convert.ToInt32(sheet.UsedRange.Row) + Convert.ToInt32(sheet.UsedRange.Rows.Count) - 1);
            // Preserve template headers and formatting; remove only its sample/previous data.
            sheet.Range["A9", "AM" + last].ClearContents();
            sheet.Range["B3"].Value2 = "개정번호 : " + revision.ToString("D4");
            // The revision date is not finalized by a simple print; do not invent it.
            sheet.Range["B4"].Value2 = "개정일자 : (진행중)";
            if (lines.Count == 0) return;
            int count = lines.Count;
            var values = new object[count, 39];
            for (int r = 0; r < count; r++)
                for (int c = 0; c < 39; c++) values[r, c] = lines[r].Cells[c];
            dynamic range = sheet.Range["A9", "AM" + (8 + count)];
            range.NumberFormat = "@";
            range.Value2 = values;
        }

        private static string? AskExportMode(Window? owner)
        {
            var dialog = new Window
            {
                Title = "대장 출력 확인", Width = 470, Height = 205, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false, Background = System.Windows.Media.Brushes.White
            };
            if (owner != null) dialog.Owner = owner;
            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(new TextBlock
            {
                Text = "현재 진행중인 대장 REV입니다. 출력 방법을 선택해 주세요.",
                FontSize = 13, Margin = new Thickness(0, 4, 0, 22), TextWrapping = TextWrapping.Wrap
            });
            var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            string? selected = null;
            foreach (var label in new[] { "개정", "단순출력", "취소" })
            {
                var button = new System.Windows.Controls.Button { Content = label, Width = 104, Height = 36, Margin = new Thickness(5, 0, 0, 0) };
                button.Click += (_, _) => { selected = label; dialog.DialogResult = true; };
                buttons.Children.Add(button);
            }
            panel.Children.Add(buttons);
            dialog.Content = panel;
            dialog.ShowDialog();
            return selected;
        }

        private void LedgerExportClick(object sender, RoutedEventArgs e)
        {
            if (RevisionCombo.SelectedItem is not Revision selected) return;
            try
            {
                long pending = ReadPendingRevision();
                bool isPending = selected.Number == pending;
                if (!isPending)
                {
                    SetStatus("과거 REV의 확정 당시 대장 데이터는 별도 보관본이 필요합니다. 현재 DB로 과거 대장을 재생성하지 않습니다.");
                    return;
                }
                string? mode = isPending ? AskExportMode(Window.GetWindow(this)) : "단순출력";
                if (mode == null || mode == "취소") { SetStatus("대장 출력을 취소했습니다."); return; }
                if (mode == "개정")
                {
                    // Revision is not changed during the initial simple-print validation.
                    // The user must explicitly choose '개정' in a separate test session.
                    if (MessageBox.Show("RM/FG 대장을 연 후 현재 REV를 확정하고 다음 REV로 변경합니다. 계속하시겠습니까?",
                        "개정 확정 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    { SetStatus("개정 작업을 취소했습니다."); return; }
                }
                var rm = ReadInventory("RM", LatestOnlyCheck.IsChecked == true);
                var fg = ReadInventory("FG", LatestOnlyCheck.IsChecked == true);
                OpenInventoryWorkbooks(rm, fg, selected.Number);
                if (mode == "개정")
                {
                    ConfirmRevision(selected.Number);
                    LoadRevisions();
                    SetStatus($"REV {selected.Number:D4} 개정 확정 완료. RM {rm.Count}행, FG {fg.Count}행 Excel 표시.");
                }
                else SetStatus($"단순출력 완료: RM {rm.Count}행, FG {fg.Count}행. 저장은 Excel에서 직접 선택하세요.");
            }
            catch (Exception ex) { ShowError("대장 출력 실패", ex); }
        }

        private static long ReadPendingRevision()
        {
            using var common = new SqliteConnection(DatabaseManager.GetConnectionString("common"));
            common.Open();
            using var cmd = common.CreateCommand();
            cmd.CommandText = "SELECT zx_code_num1 FROM zx_code_mstr WHERE zx_code_fldname='MSDS_REV_NEXT_NUMBER' AND (zx_code_value='' OR zx_code_value IS NULL)";
            var values = new List<long>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (!r.IsDBNull(0)) values.Add(Convert.ToInt64(r.GetValue(0)));
            if (values.Count != 1 || values[0] < 1) throw new InvalidOperationException("진행중 REV 설정을 확인할 수 없습니다.");
            return values[0];
        }

        private static void ConfirmRevision(long rev)
        {
            // Attach Common DB to the MSDS connection and keep both writes in one SQLite transaction.
            // A second confirmation is required before this method is called.
            var commonPath = DatabaseManager.GetDbPath("common");
            using var db = new SqliteConnection(DatabaseManager.GetConnectionString("msds"));
            db.Open();
            using (var attach = db.CreateCommand())
            {
                attach.CommandText = "ATTACH DATABASE $path AS common";
                attach.Parameters.AddWithValue("$path", commonPath);
                attach.ExecuteNonQuery();
            }
            using var tx = db.BeginTransaction();
            try
            {
                using (var verify = db.CreateCommand())
                {
                    verify.Transaction = tx;
                    verify.CommandText = "SELECT COUNT(*) FROM common.zx_code_mstr WHERE zx_code_fldname='MSDS_REV_NEXT_NUMBER' AND (zx_code_value='' OR zx_code_value IS NULL) AND zx_code_num1=$rev";
                    verify.Parameters.AddWithValue("$rev", rev);
                    if (Convert.ToInt64(verify.ExecuteScalar()) != 1)
                        throw new InvalidOperationException("진행중 REV가 변경되어 개정을 중단했습니다.");
                }
                using (var insert = db.CreateCommand())
                {
                    insert.Transaction = tx;
                    insert.CommandText = "INSERT INTO rev_mstr (rev_no,rev_date) VALUES ($rev,$date)";
                    insert.Parameters.AddWithValue("$rev", rev);
                    insert.Parameters.AddWithValue("$date", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    if (insert.ExecuteNonQuery() != 1) throw new InvalidOperationException("개정 이력 등록에 실패했습니다.");
                }
                using (var update = db.CreateCommand())
                {
                    update.Transaction = tx;
                    update.CommandText = "UPDATE common.zx_code_mstr SET zx_code_num1=$next WHERE zx_code_fldname='MSDS_REV_NEXT_NUMBER' AND (zx_code_value='' OR zx_code_value IS NULL) AND zx_code_num1=$rev";
                    update.Parameters.AddWithValue("$rev", rev);
                    update.Parameters.AddWithValue("$next", checked(rev + 1));
                    if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("다음 REV 갱신에 실패했습니다.");
                }
                tx.Commit();
            }
            catch { tx.Rollback(); throw; }
        }

        private void HistoryExportClick(object sender, RoutedEventArgs e)
        {
            if (FromRevCombo.SelectedItem is not Revision from || ToRevCombo.SelectedItem is not Revision to) return;
            if (from.Number > to.Number) { SetStatus("From REV는 To REV보다 클 수 없습니다."); return; }
            if (to.Number - from.Number > 1000) { SetStatus("개정이력 출력 범위는 최대 1,000개 REV까지 지정해 주세요."); return; }
            try
            {
                var sheets = new List<(string Name, List<string[]> Rows)>();
                using var db = new SqliteConnection(DatabaseManager.GetConnectionString("msds"));
                db.Open();
                var dateMap = new Dictionary<long, string>();
                using (var cmd = db.CreateCommand())
                {
                    var cols = Columns(db, "rev_mstr");
                    var dateCol = ColumnName(cols, "rev_date", "rev_mstr_date", "rev_revision_date", "rev_mstr_revision_date", "rev_confirmed_at", "rev_mstr_created_at", "rev_created_at", "rev_reg_date", "rev_mstr_reg_date", "rev_datetime", "rev_mstr_datetime");
                    if (dateCol.Length == 0) dateCol = cols.FirstOrDefault(c => c.Contains("date", StringComparison.OrdinalIgnoreCase)) ?? "";
                    if (dateCol.Length > 0)
                    {
                        cmd.CommandText = $"SELECT rev_no, \"{dateCol}\" FROM rev_mstr";
                        using var r = cmd.ExecuteReader();
                        while (r.Read()) if (long.TryParse(SqlText(r, 0), out long rev)) dateMap[rev] = SqlText(r, 1);
                    }
                }
                for (long rev = from.Number; rev <= to.Number; rev++)
                {
                    var newCodes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    var updatedCodes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = "SELECT audit_log_graco_code, audit_log_action FROM audit_log_hist WHERE audit_log_rev_no=$rev";
                    cmd.Parameters.AddWithValue("$rev", rev);
                    using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        var code = SqlText(r, 0); var action = SqlText(r, 1);
                        if (string.IsNullOrWhiteSpace(code)) continue;
                        if (action.Equals("INSERT", StringComparison.OrdinalIgnoreCase)) newCodes.Add(code);
                        else if (action.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)) updatedCodes.Add(code);
                    }
                    var rows = new List<string[]> { new[] { "개정번호", "개정일자", "신규(Graco Code)", "개정(Graco Code)" } };
                    var ns = newCodes.ToList(); var us = updatedCodes.ToList();
                    var count = Math.Max(1, Math.Max(ns.Count, us.Count));
                    for (int i = 0; i < count; i++) rows.Add(new[] { rev.ToString("D4"), dateMap.GetValueOrDefault(rev, ""), i < ns.Count ? ns[i] : "", i < us.Count ? us[i] : "" });
                    sheets.Add(($"REV{rev:D4}", rows));
                    if (rev == long.MaxValue) break;
                }
                OpenExcelWorkbook(sheets);
                SetStatus("개정이력 Excel을 열었습니다. 저장 여부는 Excel에서 직접 선택해 주세요.");
            }
            catch (Exception ex) { ShowError("개정이력 출력 실패", ex); }
        }

        private static void OpenExcelWorkbook(List<(string Name, List<string[]> Rows)> sheets)
        {
            // Excel COM creates an unsaved workbook in memory; no temporary or output file is written.
            var excelType = Type.GetTypeFromProgID("Excel.Application");
            if (excelType == null)
                throw new InvalidOperationException("Microsoft Excel이 설치되어 있지 않거나 COM 등록을 찾을 수 없습니다.");

            dynamic excel = Activator.CreateInstance(excelType)
                ?? throw new InvalidOperationException("Microsoft Excel을 실행할 수 없습니다.");
            dynamic? workbook = null;
            try
            {
                excel.Visible = false;
                excel.DisplayAlerts = false;
                workbook = excel.Workbooks.Add();
                for (int i = 0; i < sheets.Count; i++)
                {
                    dynamic sheet = i == 0
                        ? workbook.Worksheets[1]
                        : workbook.Worksheets.Add(Type.Missing, workbook.Worksheets[workbook.Worksheets.Count]);
                    sheet.Name = sheets[i].Name;
                    var rows = sheets[i].Rows;
                    if (rows.Count > 0)
                    {
                        var columns = rows[0].Length;
                        var data = new object[rows.Count, columns];
                        for (int r = 0; r < rows.Count; r++)
                            for (int c = 0; c < columns; c++)
                                data[r, c] = rows[r][c];
                        dynamic range = sheet.Range[sheet.Cells[1, 1], sheet.Cells[rows.Count, columns]];
                        range.NumberFormat = "@";
                        range.Value2 = data;
                        sheet.Rows[1].Font.Bold = true;
                        sheet.Columns.AutoFit();
                    }
                }
                excel.DisplayAlerts = true;
                excel.Visible = true;
                workbook.Worksheets[1].Activate();
            }
            catch
            {
                try { if (workbook != null) workbook.Close(false); } catch { }
                try { excel.Quit(); } catch { }
                throw;
            }
        }

        private void SetStatus(string message)
        {
            if (Window.GetWindow(this) is MainWindow main) main.SetRunStatus(message);
            else MessageBox.Show(message, "QCS", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ShowError(string title, Exception ex)
        {
            if (Window.GetWindow(this) is MainWindow main) main.SetRunStatus($"{title}: {ex.Message}");
            MessageBox.Show($"{title}\n{ex.Message}", "QCS", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
