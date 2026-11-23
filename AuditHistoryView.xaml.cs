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

        // Keep the ledger's CAS reference values consistent with MSDS 조회/관리:
        // first casref_id for each CAS/type, using casref_value.
        private static Dictionary<string, string[]> ReadInventoryCasReferences()
        {
            var values = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            var types = new[] { "인체", "사고", "제한", "중점", "잔류", "배출량" };
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabaseManager.GetDbPath("reference"),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };
            using var db = new SqliteConnection(builder.ToString());
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT casref_cas_no, casref_type, casref_value FROM casref_mstr
                WHERE casref_type IN ('인체','사고','제한','중점','잔류','배출량')
                ORDER BY casref_id";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string cas = reader.IsDBNull(0) ? "" : (reader.GetString(0) ?? "").Trim().Replace(" ", "");
                string type = reader.IsDBNull(1) ? "" : (reader.GetString(1) ?? "").Trim();
                if (cas.Length == 0) continue;
                int index = Array.IndexOf(types, type);
                if (index < 0) continue;
                if (!values.TryGetValue(cas, out var entries))
                {
                    entries = new string[6];
                    values.Add(cas, entries);
                }
                // Match the management view's first-row selection, even if its value is blank.
                if (entries[index] == null)
                    entries[index] = reader.IsDBNull(2) ? "" : reader.GetString(2);
            }
            return values;
        }

        private static List<InventoryLine> ReadInventory(string materialType, bool latestOnly)
        {
            var result = new List<InventoryLine>();
            var casReferences = ReadInventoryCasReferences();
            using var db = new SqliteConnection(DatabaseManager.GetConnectionString("msds"));
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT m.*,
                    COALESCE(m.msds_product_name_mod, m.msds_product_name_ai) AS msds_product_name,
                    COALESCE(m.msds_supplier_name_mod, m.msds_supplier_name_ai) AS msds_supplier_name,
                    COALESCE(m.msds_supplier_phone_mod, m.msds_supplier_phone_ai) AS msds_supplier_phone,
                    COALESCE(m.msds_supplier_address_mod, m.msds_supplier_address_ai) AS msds_supplier_address,
                    COALESCE(m.msds_revision_date_mod, m.msds_revision_date_ai) AS msds_revision_date,
                    COALESCE(m.msds_revision_no_mod, m.msds_revision_no_ai) AS msds_revision_no,
                    COALESCE(m.msds_h_codes_mod, m.msds_h_codes_ai) AS msds_h_codes,
                    COALESCE(m.msds_corrosive_flag_mod, m.msds_corrosive_flag_ai) AS msds_corrosive_flag,
                    COALESCE(m.msds_lel_mod, m.msds_lel_ai) AS msds_lel,
                    COALESCE(m.msds_uel_mod, m.msds_uel_ai) AS msds_uel,
                    COALESCE(m.msds_flash_point_mod, m.msds_flash_point_ai) AS msds_flash_point,
                    COALESCE(m.msds_autoignition_temp_mod, m.msds_autoignition_temp_ai) AS msds_autoignition_temp,
                    COALESCE(m.msds_vapor_pressure_mod, m.msds_vapor_pressure_ai) AS msds_vapor_pressure,
                    COALESCE(m.msds_reactivity_mod, m.msds_reactivity_ai) AS msds_reactivity,
                    COALESCE(m.msds_un_number_mod, m.msds_un_number_ai) AS msds_un_number,
                    COALESCE(m.msds_moel_submission_no_mod, m.msds_moel_submission_no_ai) AS msds_moel_submission_no,
                    COALESCE(m.msds_physical_state_mod, m.msds_physical_state_ai) AS msds_physical_state,
                    COALESCE(m.msds_hazardous_goods_class_mod, m.msds_hazardous_goods_class_ai) AS msds_hazardous_goods_class,
                    c.msds_comp_name,
                    c.msds_comp_cas_no,
                    COALESCE(c.msds_comp_content_raw_mod, c.msds_comp_content_raw_ai) AS msds_comp_content_raw,
                    COALESCE(c.msds_comp_content_max_mod, c.msds_comp_content_max_ai) AS msds_comp_content_max,
                    COALESCE(c.msds_comp_twa_mod, c.msds_comp_twa_ai) AS msds_comp_twa,
                    COALESCE(c.msds_comp_stel_mod, c.msds_comp_stel_ai) AS msds_comp_stel,
                    COALESCE(c.msds_comp_oral_ld50_mod, c.msds_comp_oral_ld50_ai) AS msds_comp_oral_ld50,
                    COALESCE(c.msds_comp_dermal_ld50_mod, c.msds_comp_dermal_ld50_ai) AS msds_comp_dermal_ld50,
                    COALESCE(c.msds_comp_inhalation_lc50_mod, c.msds_comp_inhalation_lc50_ai) AS msds_comp_inhalation_lc50,
                    COALESCE(m.msds_molecular_formula_mod, m.msds_molecular_formula_ai) AS msds_comp_molecular_formula
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
                    cells[28] = V("msds_physical_state"); // AC
                    cells[29] = V("msds_hazardous_goods_class"); // AD
                }
                cells[9] = V("msds_comp_name"); cells[10] = V("msds_comp_cas_no");
                cells[11] = V("msds_comp_content_raw"); cells[12] = V("msds_comp_content_max");
                cells[13] = V("msds_comp_molecular_formula");
                cells[14] = V("msds_comp_twa"); cells[15] = V("msds_comp_stel");
                cells[22] = V("msds_comp_oral_ld50"); // W: 경구 LD50
                cells[23] = V("msds_comp_dermal_ld50"); // X: 경피 LD50
                cells[24] = V("msds_comp_inhalation_lc50"); // Y: 흡입 LC50
                string cas = V("msds_comp_cas_no").Trim().Replace(" ", "");
                if (casReferences.TryGetValue(cas, out var referenceValues))
                    for (int i = 0; i < 6; i++) cells[30 + i] = referenceValues[i] ?? ""; // AE:AJ
                result.Add(new InventoryLine(cells));
            }
            return result;
        }

        private static string TemplatePath(string materialType)
        {
            string name = materialType == "RM" ? "원료 유해위험물질목록.xltm" : "제품 유해위험물질목록.xltm";
            string exePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("QCS 실행 파일 경로를 확인할 수 없습니다.");
            string exeDirectory = Path.GetDirectoryName(exePath)
                ?? throw new InvalidOperationException("QCS 실행 폴더를 확인할 수 없습니다.");
            string path = Path.Combine(exeDirectory, "QCS_xltm", name);
            if (!File.Exists(path)) throw new FileNotFoundException("Excel 템플릿이 실행 폴더의 QCS_xltm에 없습니다.", path);
            return path;
        }

        private static void OpenInventoryWorkbooks(List<InventoryLine>? rm, List<InventoryLine>? fg, long revision)
        {
            if (rm == null && fg == null) return;
            var excelType = Type.GetTypeFromProgID("Excel.Application")
                ?? throw new InvalidOperationException("Microsoft Excel이 설치되어 있지 않습니다.");
            dynamic excel = Activator.CreateInstance(excelType)
                ?? throw new InvalidOperationException("Microsoft Excel을 실행할 수 없습니다.");
            dynamic? rmBook = null, fgBook = null;
            try
            {
                excel.Visible = false;
                excel.DisplayAlerts = false;
                excel.AutomationSecurity = 3;
                if (rm != null)
                {
                    rmBook = excel.Workbooks.Add(TemplatePath("RM"));
                    FillInventorySheet(rmBook, rm, revision);
                }
                if (fg != null)
                {
                    fgBook = excel.Workbooks.Add(TemplatePath("FG"));
                    FillInventorySheet(fgBook, fg, revision);
                }
                excel.DisplayAlerts = true;
                excel.Visible = true;
                if (rmBook != null) rmBook.Worksheets[1].Activate();
                else if (fgBook != null) fgBook.Worksheets[1].Activate();
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
            // Excel constants: xlLeft = -4131, xlTop = -4160.
            // Apply to data rows only; preserve template header alignment.
            range.HorizontalAlignment = -4131;
            range.VerticalAlignment = -4160;
        }

        private sealed record ExportChoice(string Material, string Mode);

        private ExportChoice? AskExportMode(Window? owner, long revision)
        {
            var dialog = new Window
            {
                Title = "대장 출력",
                Width = 520, Height = 350,
                MinWidth = 520, MinHeight = 350,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Background = System.Windows.Media.Brushes.White,
                FontFamily = new System.Windows.Media.FontFamily("Malgun Gothic"),
                FontSize = 13
            };
            if (owner != null) dialog.Owner = owner;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new Border
            {
                Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#214B7E")!,
                Padding = new System.Windows.Thickness(20, 15, 20, 15)
            };
            header.Child = new TextBlock
            {
                Text = $"대장 출력  ·  REV {revision:D4} (진행중)",
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold
            };
            root.Children.Add(header);

            var body = new StackPanel { Margin = new Thickness(24, 20, 24, 12) };
            Grid.SetRow(body, 1);
            body.Children.Add(new TextBlock
            {
                Text = "출력할 대장을 선택해 주세요.",
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#1E3A5F")!,
                Margin = new Thickness(0, 0, 0, 13)
            });
            var choices = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            System.Windows.Controls.RadioButton CreateChoice(string label, string tag, bool selected)
            {
                var option = new System.Windows.Controls.RadioButton
                {
                    Content = label, Tag = tag, GroupName = "LedgerExportMaterial",
                    IsChecked = selected, Margin = new Thickness(0, 0, 22, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                choices.Children.Add(option);
                return option;
            }
            var both = CreateChoice("원료 + 제품", "BOTH", true);
            var rmOnly = CreateChoice("원료(RM)", "RM", false);
            var fgOnly = CreateChoice("제품(FG)", "FG", false);
            body.Children.Add(choices);
            var hint = new Border
            {
                Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#F0F4F9")!,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12), Margin = new Thickness(0, 22, 0, 0)
            };
            hint.Child = new TextBlock
            {
                Text = "단순출력은 선택한 대장만 Excel에서 엽니다.\n개정 확정은 원료와 제품을 함께 출력할 때만 가능합니다.",
                Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#52677F")!,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21
            };
            body.Children.Add(hint);
            root.Children.Add(body);

            var footer = new Border
            {
                BorderBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#DCE4EF")!,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new System.Windows.Thickness(20, 13, 20, 13)
            };
            Grid.SetRow(footer, 2);
            var buttons = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            ExportChoice? selectedChoice = null;
            string SelectedMaterial() => rmOnly.IsChecked == true ? "RM" : fgOnly.IsChecked == true ? "FG" : "BOTH";
            System.Windows.Controls.Button CreateButton(string label, bool primary, Action action)
            {
                var button = new System.Windows.Controls.Button
                {
                    Content = label, Width = 112, Height = 36,
                    Margin = new Thickness(6, 0, 0, 0),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                if (primary)
                {
                    // Use the same explicit QCS primary button style as the main screen.
                    if (TryFindResource("QcsPrimaryButton") is Style style) button.Style = style;
                }
                else
                {
                    button.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#F1F5F9")!;
                    button.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#1E3A5F")!;
                    button.BorderBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#CBD5E1")!;
                }
                button.Click += (_, _) => action();
                buttons.Children.Add(button);
                return button;
            }
            var revise = CreateButton("개정", true, () =>
            {
                if (SelectedMaterial() != "BOTH") return;
                selectedChoice = new ExportChoice("BOTH", "개정");
                dialog.DialogResult = true;
            });
            CreateButton("단순출력", true, () =>
            {
                selectedChoice = new ExportChoice(SelectedMaterial(), "단순출력");
                dialog.DialogResult = true;
            });
            CreateButton("취소", false, () => dialog.DialogResult = false);
            void UpdateRevisionAvailability() => revise.IsEnabled = both.IsChecked == true;
            both.Checked += (_, _) => UpdateRevisionAvailability();
            rmOnly.Checked += (_, _) => UpdateRevisionAvailability();
            fgOnly.Checked += (_, _) => UpdateRevisionAvailability();
            UpdateRevisionAvailability();
            footer.Child = buttons;
            root.Children.Add(footer);
            dialog.Content = root;
            return dialog.ShowDialog() == true ? selectedChoice : null;
        }

        private void LedgerExportClick(object sender, RoutedEventArgs e)
        {
            if (RevisionCombo.SelectedItem is not Revision selected) return;
            try
            {
                long pending = ReadPendingRevision();
                if (selected.Number != pending)
                {
                    SetStatus("과거 REV의 확정 당시 대장 데이터는 별도 보관본이 필요합니다. 현재 DB로 과거 대장을 재생성하지 않습니다.");
                    return;
                }
                var choice = AskExportMode(Window.GetWindow(this), selected.Number);
                if (choice == null) { SetStatus("대장 출력을 취소했습니다."); return; }
                if (choice.Mode == "개정")
                {
                    if (MessageBox.Show("RM/FG 대장을 연 후 현재 REV를 확정하고 다음 REV로 변경합니다. 계속하시겠습니까?",
                        "개정 확정 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    { SetStatus("개정 작업을 취소했습니다."); return; }
                }
                var rm = choice.Material != "FG" ? ReadInventory("RM", LatestOnlyCheck.IsChecked == true) : null;
                var fg = choice.Material != "RM" ? ReadInventory("FG", LatestOnlyCheck.IsChecked == true) : null;
                OpenInventoryWorkbooks(rm, fg, selected.Number);
                if (choice.Mode == "개정")
                {
                    ConfirmRevision(selected.Number);
                    LoadRevisions();
                    SetStatus($"REV {selected.Number:D4} 개정 확정 완료. RM {rm?.Count ?? 0}행, FG {fg?.Count ?? 0}행 Excel 표시.");
                }
                else
                {
                    var targets = choice.Material == "BOTH" ? "RM/FG" : choice.Material;
                    SetStatus($"단순출력 완료: {targets} (RM {rm?.Count ?? 0}행, FG {fg?.Count ?? 0}행). 저장은 Excel에서 직접 선택하세요.");
                }
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
