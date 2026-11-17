using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.ComponentModel;
using System.Diagnostics;
using ExcelDataReader;
using Microsoft.Data.Sqlite;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using UserControl = System.Windows.Controls.UserControl;
using MessageBox = System.Windows.MessageBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace QCS
{
    public partial class ReferenceDataView : UserControl
    {
        private static readonly string[] Types = { "인체", "사고", "제한", "중점", "잔류", "배출량" };
        private static readonly string[] Fields = { "casref_src_no", "casref_cas_no", "casref_name_en", "casref_name_ko", "casref_exist_code", "casref_hazard", "casref_accident", "casref_restrict", "casref_priority", "casref_persist", "casref_value", "casref_reg_target", "casref_exist_yn", "casref_char#1" };
        private sealed class PendingFile
        {
            public string Path { get; set; } = "";
            public string Type { get; set; } = "";
            public string Name => System.IO.Path.GetFileName(Path);
        }
        private sealed class ReferenceSummary
        {
            public string Type { get; set; } = "";
            public string Count { get; set; } = "미등록";
            public string Updated { get; set; } = "-";
            public string User { get; set; } = "-";
        }
        private readonly ObservableCollection<PendingFile> _files = new();
        private readonly ObservableCollection<ReferenceSummary> _summaries = new();
        private string _type = "전체";
        private DataTable? _current;
        private bool _loaded;
        private bool _resetting;
        private string? _sortColumn;
        private ListSortDirection? _sortDirection;
        private readonly Dictionary<DataGridColumn, string> _headerNames = new();

        public ReferenceDataView()
        {
            InitializeComponent();
            SelectedFilesList.ItemsSource = _files;
            SummaryList.ItemsSource = _summaries;
            UpdatePendingFiles();
            Loaded += (_, __) => { _loaded = true; RefreshData(); };
        }
        private void Notify(string message)
        {
            StatusText.Text = message;
            if (System.Windows.Window.GetWindow(this) is MainWindow main) main.SetRunStatus(message);
        }
        private void Type_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TypeCombo?.SelectedItem is ComboBoxItem item)
                _type = Convert.ToString(item.Content) ?? "전체";
            if (_loaded && !_resetting) RefreshData();
        }
        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { RefreshData(); e.Handled = true; }
        }
        private static string? DetectType(string filename)
        {
            var hits = Types.Where(t => Path.GetFileNameWithoutExtension(filename).Contains(t, StringComparison.Ordinal)).ToArray();
            return hits.Length == 1 ? hits[0] : null;
        }
        private void UpdatePendingFiles()
        {
            SelectedFilesList.Visibility = _files.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyFilesText.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SelectedCountText.Text = _files.Count == 0 ? "" : $"선택 {_files.Count}개";
            bool valid = _files.Count > 0 && _files.All(f => !string.IsNullOrWhiteSpace(f.Type))
                         && _files.GroupBy(f => f.Type).All(g => g.Count() == 1);
            UploadButton.IsEnabled = valid;
            ClearFilesButton.IsEnabled = _files.Count > 0;
        }
        private void SelectFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = "참조자료 Excel 파일 선택", Filter = "Excel 파일 (*.xlsx;*.xlsm;*.xls)|*.xlsx;*.xlsm;*.xls", Multiselect = true };
            if (dialog.ShowDialog() != true) return;
            var errors = new List<string>();
            foreach (string path in dialog.FileNames)
            {
                if (_files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                string? type = DetectType(path);
                if (type == null) { errors.Add(Path.GetFileName(path) + ": 유형을 식별할 수 없습니다."); continue; }
                if (_files.Any(f => f.Type == type)) { errors.Add(Path.GetFileName(path) + $": {type} 유형이 이미 선택되어 있습니다."); continue; }
                _files.Add(new PendingFile { Path = path, Type = type });
            }
            UpdatePendingFiles();
            Notify($"등록 대기 파일 {_files.Count}개" + (errors.Count > 0 ? $" / 제외 {errors.Count}개" : ""));
            if (errors.Count > 0) MessageBox.Show(string.Join("\n", errors), "파일 선택 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        private void RemoveFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.DataContext is PendingFile file)
                _files.Remove(file);
            UpdatePendingFiles();
            Notify($"등록 대기 파일 {_files.Count}개");
        }
        private void ClearFiles_Click(object sender, RoutedEventArgs e)
        {
            _files.Clear(); UpdatePendingFiles(); Notify("선택한 파일을 모두 제거했습니다. DB 자료는 변경되지 않았습니다.");
        }
        private static string Cell(IExcelDataReader reader, int col)
        {
            if (col >= reader.FieldCount || reader.IsDBNull(col)) return "";
            object value = reader.GetValue(col);
            return value is DateTime date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";
        }
        private sealed class ImportFile
        {
            public string Type { get; init; } = "";
            public List<string[]> Rows { get; } = new();
        }
        // Some supplied workbooks incorrectly declare A1 as the entire used range.
        // Normalize only an in-memory copy; the original Excel file is never changed.
        private static Stream NormalizeWorkbookDimension(Stream source, string extension)
        {
            if (!extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase)) return source;
            var output = new MemoryStream();
            source.Position = 0;
            using (var archive = new ZipArchive(source, ZipArchiveMode.Read, true))
            using (var copy = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                foreach (var entry in archive.Entries)
                {
                    var target = copy.CreateEntry(entry.FullName, CompressionLevel.Fastest);
                    using var dest = target.Open();
                    using var original = entry.Open();
                    if (entry.FullName == "xl/worksheets/sheet1.xml")
                    {
                        using var reader = new StreamReader(original, Encoding.UTF8);
                        string xml = reader.ReadToEnd();
                        if (Regex.IsMatch(xml, "<dimension\\s+ref=\"A1\""))
                        {
                            var rows = Regex.Matches(xml, "<row\\s+r=\"([0-9]+)\"");
                            int last = rows.Count == 0 ? 1 : int.Parse(rows[rows.Count - 1].Groups[1].Value, CultureInfo.InvariantCulture);
                            xml = Regex.Replace(xml, "<dimension\\s+ref=\"A1\"\\s*/>", $"<dimension ref=\"A1:M{last}\"/>", RegexOptions.None);
                        }
                        byte[] bytes = Encoding.UTF8.GetBytes(xml);
                        dest.Write(bytes, 0, bytes.Length);
                    }
                    else original.CopyTo(dest);
                }
            }
            output.Position = 0;
            return output;
        }
        private static ImportFile ReadExcel(string file, string type)
        {
            var result = new ImportFile { Type = type };
            using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var normalized = NormalizeWorkbookDimension(stream, Path.GetExtension(file));
            // ExcelDataReader requires legacy code pages (e.g. Windows-1252) for some workbooks.
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            using var reader = ExcelReaderFactory.CreateReader(normalized);
            int excelRow = 0;
            while (reader.Read())
            {
                excelRow++;
                string[] raw = Enumerable.Range(0, type == "배출량" ? 6 : 13).Select(i => Cell(reader, i)).ToArray();
                if (type != "배출량" && excelRow == 1)
                {
                    if (raw[0] != "NO" || !raw[1].Contains("CAS")) throw new InvalidDataException($"{Path.GetFileName(file)}: A/B열 제목이 예상 양식과 다릅니다.");
                    continue;
                }
                // Skip emission-report headings, but keep actual rows with an empty CAS No.
                if (type == "배출량" && (raw[1] == "CAS번호" ||
                    (string.IsNullOrWhiteSpace(raw[1]) && raw.Skip(2).All(string.IsNullOrWhiteSpace)))) continue;
                if (raw.All(string.IsNullOrWhiteSpace)) continue;
                // CAS No. 공란도 원본 값(빈 문자열) 그대로 등록합니다. 전체 공란 행은 위에서 제외합니다.
                string[] values = new string[Fields.Length];
                if (type == "배출량")
                {
                    // 배출량: A=NO, B=CAS, C=국문, D=영문, E=함량기준, F=대상여부
                    values[0] = raw[0]; values[1] = raw[1]; values[2] = raw[3]; values[3] = raw[2];
                    values[10] = raw[5]; values[13] = raw[4];
                }
                else Array.Copy(raw, values, raw.Length);
                result.Rows.Add(values);
            }
            if (result.Rows.Count == 0) throw new InvalidDataException(Path.GetFileName(file) + ": 등록할 데이터가 없습니다.");
            return result;
        }
        private static SqliteConnection OpenDb(bool write)
        {
            string path = DatabaseManager.GetDbPath("reference");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("참조 DB 파일이 없습니다.", path);
            var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = write ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly, DefaultTimeout = 30, Pooling = false };
            var connection = new SqliteConnection(cs.ToString());
            connection.Open();
            return connection;
        }
        private void Upload_Click(object sender, RoutedEventArgs e)
        {
            if (_files.Count == 0 || !UploadButton.IsEnabled) return;
            UploadButton.IsEnabled = false;
            try
            {
                // Validate every selected workbook before any DB change.
                var imports = _files.Select(f => ReadExcel(f.Path, f.Type)).ToList();
                string summary = string.Join("\n", imports.Select(x => $"{x.Type}: {x.Rows.Count:N0}건"));
                if (MessageBox.Show("다음 유형의 기존 자료가 전체 교체됩니다.\n\n" + summary + "\n\n진행하시겠습니까?", "참조자료 일괄 등록 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                { Notify("참조자료 등록 취소"); return; }
                using var conn = OpenDb(true);
                using var transaction = conn.BeginTransaction();
                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                foreach (var file in imports)
                {
                    using (var delete = conn.CreateCommand())
                    {
                        delete.Transaction = transaction;
                        delete.CommandText = "DELETE FROM casref_mstr WHERE casref_type = $type";
                        delete.Parameters.AddWithValue("$type", file.Type);
                        delete.ExecuteNonQuery();
                    }
                    foreach (var row in file.Rows)
                    {
                        using var insert = conn.CreateCommand();
                        insert.Transaction = transaction;
                        string[] quoted = Fields.Select(f => "\"" + f + "\"").ToArray();
                        string[] parameters = Enumerable.Range(0, Fields.Length).Select(i => "$p" + i).ToArray();
                        insert.CommandText = $"INSERT INTO casref_mstr (casref_type, {string.Join(",", quoted)}, casref_upload_dt, casref_upload_id) VALUES ($type, {string.Join(",", parameters)}, $stamp, $user)";
                        // Set an explicit SQLite type and value for every parameter, including empty Excel cells.
                        insert.Parameters.Add("$type", SqliteType.Text).Value = file.Type;
                        insert.Parameters.Add("$stamp", SqliteType.Text).Value = stamp;
                        insert.Parameters.Add("$user", SqliteType.Text).Value = Environment.UserName;
                        if (row.Length != Fields.Length)
                            throw new InvalidDataException($"{file.Type}: 필드 개수 불일치 (데이터 {row.Length}, DB {Fields.Length})");
                        for (int i = 0; i < Fields.Length; i++)
                            insert.Parameters.Add(parameters[i], SqliteType.Text).Value = row[i] ?? string.Empty;
                        insert.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
                _files.Clear(); UpdatePendingFiles();
                RefreshData();
                Notify("참조자료 등록 완료: " + string.Join(", ", imports.Select(x => x.Type + " " + x.Rows.Count.ToString("N0") + "건")));
            }
            catch (Exception ex)
            {
                Notify("참조자료 등록 실패: " + ex.Message);
                MessageBox.Show("등록되지 않았습니다. 기존 데이터는 유지됩니다.\n" + ex.Message, "참조자료 등록 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { UpdatePendingFiles(); }
        }
        private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshData();
        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _resetting = true;
            try { SearchBox.Clear(); TypeCombo.SelectedIndex = 0; _type = "전체"; }
            finally { _resetting = false; }
            RefreshData();
        }
        private void RefreshData()
        {
            try
            {
                using var conn = OpenDb(false);
                using (var summary = conn.CreateCommand())
                {
                    summary.CommandText = "SELECT t.casref_type, COUNT(*), MAX(t.casref_upload_dt), (SELECT u.casref_upload_id FROM casref_mstr u WHERE u.casref_type = t.casref_type ORDER BY u.casref_upload_dt DESC, u.casref_id DESC LIMIT 1) FROM casref_mstr t GROUP BY t.casref_type";
                    var map = new Dictionary<string, (long Count, string Date, string User)>();
                    using var r = summary.ExecuteReader();
                    while (r.Read())
                    {
                        string type = r.GetString(0);
                        string date = r.IsDBNull(2) ? "" : Convert.ToString(r.GetValue(2)) ?? "";
                        string user = r.IsDBNull(3) ? "" : Convert.ToString(r.GetValue(3)) ?? "";
                        map[type] = (r.GetInt64(1), date, user);
                    }
                    _summaries.Clear();
                    foreach (string t in Types)
                    {
                        if (map.TryGetValue(t, out var v))
                            _summaries.Add(new ReferenceSummary { Type = t, Count = v.Count.ToString("N0"), Updated = v.Date, User = v.User });
                        else _summaries.Add(new ReferenceSummary { Type = t });
                    }
                }
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT casref_src_no AS 'NO', casref_type AS '자료 유형', casref_cas_no AS 'CAS No.', casref_name_ko AS '물질명', casref_name_en AS '영문명', casref_exist_code AS '기존', casref_hazard AS '급성·만성·생태', casref_accident AS '사고대비', casref_restrict AS '제한/금지/허가', casref_priority AS '중점', casref_persist AS '잔류', casref_value AS '유해특성/대상', casref_reg_target AS '등록대상', casref_exist_yn AS '기존물질여부', ""casref_char#1"" AS '함량기준', casref_upload_dt AS '최종 등록일' FROM casref_mstr WHERE ($type = '전체' OR casref_type = $type) AND ($search = '' OR casref_cas_no LIKE '%' || $search || '%' OR casref_name_ko LIKE '%' || $search || '%' OR casref_name_en LIKE '%' || $search || '%' OR casref_value LIKE '%' || $search || '%' OR casref_exist_code LIKE '%' || $search || '%' OR casref_hazard LIKE '%' || $search || '%' OR casref_accident LIKE '%' || $search || '%' OR casref_restrict LIKE '%' || $search || '%' OR casref_priority LIKE '%' || $search || '%' OR casref_persist LIKE '%' || $search || '%' OR casref_reg_target LIKE '%' || $search || '%' OR ""casref_char#1"" LIKE '%' || $search || '%') ORDER BY casref_type, casref_id";
                cmd.Parameters.AddWithValue("$type", _type);
                cmd.Parameters.AddWithValue("$search", SearchBox.Text.Trim());
                using var reader = cmd.ExecuteReader();
                var table = new DataTable();
                for (int i = 0; i < reader.FieldCount; i++) table.Columns.Add(reader.GetName(i), typeof(string));
                while (reader.Read())
                {
                    var row = table.NewRow();
                    for (int i = 0; i < reader.FieldCount; i++) row[i] = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i)) ?? "";
                    table.Rows.Add(row);
                }
                _current = table;
                _sortColumn = null;
                _sortDirection = null;
                _headerNames.Clear();
                ResultsGrid.ItemsSource = table.DefaultView;
                CountText.Text = $"참조자료 목록  |  조회 {table.Rows.Count:N0}건";
                Notify($"참조자료 조회 완료: {table.Rows.Count:N0}건");
            }
            catch (Exception ex)
            {
                _current = null; ResultsGrid.ItemsSource = null;
                _summaries.Clear();
                Notify("참조자료 조회 실패: " + ex.Message);
            }
        }
        private void DeleteAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var conn = OpenDb(true);
                using var count = conn.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM casref_mstr";
                long total = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (total == 0) { Notify("삭제할 참조자료가 없습니다."); return; }
                if (MessageBox.Show($"참조자료 6개 유형의 전체 {total:N0}건을 삭제합니다.\n이 작업은 취소할 수 없습니다.\n\n정말 전체 삭제하시겠습니까?", "참조자료 전체 삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                { Notify("참조자료 전체 삭제 취소"); return; }
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM casref_mstr";
                int removed = cmd.ExecuteNonQuery();
                tx.Commit();
                RefreshData();
                Notify($"참조자료 전체 삭제 완료: {removed:N0}건");
            }
            catch (Exception ex)
            {
                Notify("참조자료 전체 삭제 실패: " + ex.Message);
                MessageBox.Show(ex.Message, "참조자료 삭제 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void ResultsGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            // Preserve text display for all DB-backed string columns, including CAS No.
            if (e.Column is DataGridTextColumn textColumn)
                textColumn.Binding = new System.Windows.Data.Binding("[" + e.PropertyName + "]");
            e.Column.SortMemberPath = e.PropertyName;
            _headerNames[e.Column] = e.PropertyName;
        }
        private void ResultsGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;
            if (_current == null) return;
            string column = e.Column.SortMemberPath;
            if (string.IsNullOrEmpty(column) || !_current.Columns.Contains(column)) return;

            // Single-column cycle: ascending -> descending -> original query order.
            if (_sortColumn != column || !_sortDirection.HasValue)
            {
                _sortColumn = column;
                _sortDirection = ListSortDirection.Ascending;
            }
            else if (_sortDirection == ListSortDirection.Ascending)
                _sortDirection = ListSortDirection.Descending;
            else
            {
                _sortColumn = null;
                _sortDirection = null;
            }

            _current.DefaultView.Sort = _sortColumn == null
                ? string.Empty
                : "[" + _sortColumn.Replace("]", "\\]") + "] "
                    + (_sortDirection == ListSortDirection.Ascending ? "ASC" : "DESC");

            foreach (DataGridColumn gridColumn in ResultsGrid.Columns)
            {
                if (!_headerNames.TryGetValue(gridColumn, out string? name)) continue;
                bool selected = name == _sortColumn && _sortDirection.HasValue;
                // Show the arrow as header text even when a shared template hides sort glyphs.
                gridColumn.Header = selected
                    ? name + (_sortDirection == ListSortDirection.Ascending ? " ▲" : " ▼")
                    : name;
                gridColumn.SortDirection = selected ? _sortDirection : null;
            }
        }
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || _current.Rows.Count == 0)
            {
                Notify("Excel에서 열 조회 결과가 없습니다.");
                return;
            }

            dynamic? excel = null;
            try
            {
                // Excel COM automation: new unsaved workbook, no temporary CSV.
                Type? excelType = Type.GetTypeFromProgID("Excel.Application");
                if (excelType == null)
                    throw new InvalidOperationException("Microsoft Excel이 설치되어 있지 않거나 COM 등록을 찾을 수 없습니다.");

                excel = Activator.CreateInstance(excelType)
                    ?? throw new InvalidOperationException("Excel을 실행할 수 없습니다.");
                excel.Visible = true;
                dynamic workbook = excel.Workbooks.Add();
                dynamic worksheet = workbook.Worksheets[1];

                int rowCount = _current.DefaultView.Count;
                int columnCount = _current.Columns.Count;
                var values = new object[rowCount + 1, columnCount];
                for (int c = 0; c < columnCount; c++)
                    values[0, c] = _current.Columns[c].ColumnName;
                int r = 1;
                foreach (DataRowView row in _current.DefaultView)
                {
                    for (int c = 0; c < columnCount; c++)
                        values[r, c] = Convert.ToString(row[c]) ?? "";
                    r++;
                }

                dynamic firstCell = worksheet.Cells[1, 1];
                dynamic lastCell = worksheet.Cells[rowCount + 1, columnCount];
                dynamic targetRange = worksheet.Range[firstCell, lastCell];
                targetRange.NumberFormat = "@"; // Preserve CAS numbers as text.
                targetRange.Value2 = values;
                dynamic headerRange = worksheet.Range[worksheet.Cells[1, 1], worksheet.Cells[1, columnCount]];
                headerRange.Font.Bold = true;
                headerRange.AutoFilter();
                worksheet.Columns.AutoFit();
                worksheet.Activate();

                Notify($"Excel에서 조회 결과 열기: {rowCount:N0}건 (저장 여부는 Excel에서 선택)");
            }
            catch (Exception ex)
            {
                Notify("Excel 실행 실패: " + ex.Message);
                MessageBox.Show("Excel에 조회 결과를 표시할 수 없습니다.\n" + ex.Message,
                    "Excel 실행 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
