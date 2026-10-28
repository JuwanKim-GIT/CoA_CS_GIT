using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Input;
using QCS.Analysis;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Microsoft.Data.Sqlite;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

// WPF와 Windows Forms 클래스 이름 충돌 방지
using UserControl = System.Windows.Controls.UserControl;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;


namespace QCS
{
    public partial class MsdsRegistrationView : UserControl
    {
        private sealed class PdfItem
        {
            public int Index { get; set; }
            public string FileName { get; set; } = "";
            public string FileSize
            {
                get
                {
                    if (!File.Exists(FullPath)) return "";
                    long bytes = new FileInfo(FullPath).Length;
                    return bytes >= 1048576 ? $"{bytes / 1048576.0:F1} MB" : $"{bytes / 1024.0:F0} KB";
                }
            }
            public string FullPath { get; set; } = "";
            public string Status { get; set; } = "대기";
            public MsdsAnalysisResult? Parsed { get; set; }
            public string AnalysisStatus { get; set; } = "대기";
            public string AnalysisDisplay { get; set; } = "대기";
            public bool Confirmed { get; set; }
            public bool Dirty { get; set; }
            public System.Collections.Generic.Dictionary<string, string>? SavedValues { get; set; }
            public System.Collections.Generic.List<IngredientEdit>? SavedIngredients { get; set; }
        }

        private sealed class IngredientEdit
        {
            public int Index { get; set; }
            public string Name { get; set; } = "";
            public string CasNumber { get; set; } = "";
            public string ConcentrationRaw { get; set; } = "";
        }

        private PdfItem? _displayedItem;
        private bool _loadingFields;
        private bool _revertingSelection;
        private readonly string[] _editableFieldNames =
        {
            "ProductNameBox", "SupplierNameBox", "GracoCodeBox", "RevisionDateBox",
            "RevisionNumberBox", "PhoneBox", "AddressBox", "UnNumberBox", "HazardCodesBox",
            "AutoIgnitionBox", "VaporPressureBox", "LelBox", "UelBox", "FlashPointBox",
            "CorrosivityBox", "PersonalProtectionBox"
        };
        private System.Collections.Generic.Dictionary<string, string> CaptureFields()
        {
            var values = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var name in _editableFieldNames)
                if (FindName(name) is System.Windows.Controls.TextBox box) values[name] = box.Text;
            return values;
        }
        private System.Collections.Generic.List<IngredientEdit> CaptureIngredients() =>
            IngredientsGrid.Items.OfType<IngredientEdit>().Select(v => new IngredientEdit
            { Index = v.Index, Name = v.Name, CasNumber = v.CasNumber,
              ConcentrationRaw = v.ConcentrationRaw }).ToList();
        private void SetStatus(PdfItem item, string value)
        {
            item.Status = value;
            FilesGrid.Items.Refresh();
        }
        private void SaveDisplayed()
        {
            if (_displayedItem == null || _isAnalyzing || _displayedItem.Parsed == null) return;
            IngredientsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            IngredientsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
            _displayedItem.SavedValues = CaptureFields();
            _displayedItem.SavedIngredients = CaptureIngredients();
            _displayedItem.Confirmed = true;
            _displayedItem.Dirty = false;
            SetStatus(_displayedItem, "확인완료");
        }
        private void SaveCurrent_Click(object sender, RoutedEventArgs e) => SaveDisplayed();

        private async void DbSave_Click(object sender, RoutedEventArgs e)
        {
            if (_isAnalyzing) return;
            // 현재 편집 중인 셀까지 반영한다.
            IngredientsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            IngredientsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (_displayedItem?.Dirty == true)
            {
                System.Windows.MessageBox.Show("현재 PDF의 수정사항을 먼저 [저장]해 주세요.", "DB 저장", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var targets = _files.Where(v => v.Confirmed && v.SavedValues != null && v.SavedIngredients != null).ToList();
            if (targets.Count == 0)
            {
                System.Windows.MessageBox.Show("DB에 저장할 확인완료 항목이 없습니다.", "DB 저장");
                return;
            }
            if (System.Windows.MessageBox.Show($"확인완료 {targets.Count}건을 DB에 저장하시겠습니까?", "DB 저장 확인",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            DbSaveButton.IsEnabled = false;
            int success = 0, failed = 0;
            try
            {
                foreach (var item in targets)
                {
                    var values = new Dictionary<string, string>(item.SavedValues!);
                    var ingredients = item.SavedIngredients!.Select(v => new MsdsDbRegistration.Ingredient
                    {
                        Name = v.Name, CasNumber = v.CasNumber, ConcentrationRaw = v.ConcentrationRaw,
                        SortOrder = v.Index
                    }).ToList();
                    var snapshot = new MsdsDbRegistration.Document
                    {
                        FilePath = item.FullPath, Values = values, Ingredients = ingredients
                    };
                    var result = await Task.Run(() => MsdsDbRegistration.Save(snapshot));
                    if (result.Success)
                    {
                        if (ReferenceEquals(_displayedItem, item)) _displayedItem = null;
                        _files.Remove(item);
                        success++;
                    }
                    else
                    {
                        SetStatus(item, result.Stage == "PDF" ? "PDF복사실패" : "DB저장실패");
                        failed++;
                        System.Windows.MessageBox.Show($"{item.FileName}\n{result.Message}", "DB 저장 오류",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            finally
            {
                FilesGrid.Items.Refresh();
                UpdateCount();
                DbSaveButton.IsEnabled = true;
            }
            System.Windows.MessageBox.Show($"DB 저장 완료: 성공 {success}건 / 실패 {failed}건", "DB 저장 결과");
        }

        private void AddIngredient_Click(object sender, RoutedEventArgs e)
        {
            if (_displayedItem?.Parsed == null || _isAnalyzing) return;
            IngredientsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            IngredientsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var rows = CaptureIngredients();
            rows.Add(new IngredientEdit { Index = rows.Count + 1 });
            IngredientsGrid.ItemsSource = rows;
            IngredientsGrid.SelectedItem = rows[^1];
            IngredientsGrid.ScrollIntoView(rows[^1]);
            MarkDirty();
        }

        private void RemoveIngredient_Click(object sender, RoutedEventArgs e)
        {
            if (_displayedItem?.Parsed == null || _isAnalyzing) return;
            if (IngredientsGrid.SelectedItem is not IngredientEdit selected) return;
            IngredientsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            IngredientsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var rows = CaptureIngredients();
            int index = IngredientsGrid.Items.OfType<IngredientEdit>().ToList().IndexOf(selected);
            if (index < 0) return;
            rows.RemoveAt(index);
            for (int i = 0; i < rows.Count; i++) rows[i].Index = i + 1;
            IngredientsGrid.ItemsSource = rows;
            MarkDirty();
        }

        private void EditableField_TextChanged(object sender, TextChangedEventArgs e) => MarkDirty();
        private void IngredientsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e) =>
            Dispatcher.BeginInvoke(new Action(MarkDirty), System.Windows.Threading.DispatcherPriority.Background);
        private void MarkDirty()
        {
            if (_loadingFields || _isAnalyzing || _displayedItem?.Parsed == null) return;
            _displayedItem.Dirty = true;
            if (_displayedItem.Confirmed)
            {
                _displayedItem.Confirmed = false;
                SetStatus(_displayedItem, _displayedItem.AnalysisDisplay);
            }
        }
        private bool ConfirmLeave()
        {
            if (_displayedItem?.Dirty != true)
            {
                // 분석 결과를 수정하지 않았다면 파일 이동 시 자동 확인완료 처리한다.
                if (_displayedItem?.Parsed != null && !_displayedItem.Confirmed && !_isAnalyzing)
                    SaveDisplayed();
                return true;
            }
            var result = System.Windows.MessageBox.Show(
                "현재 파일의 변경사항을 저장하시겠습니까?\n예: 저장 / 아니요: 저장 안 함 / 취소: 이동 취소",
                "변경사항 저장 확인", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel) return false;
            if (result == MessageBoxResult.Yes) SaveDisplayed();
            else
            {
                _displayedItem.Dirty = false;
                _displayedItem.Confirmed = _displayedItem.SavedValues != null;
                SetStatus(_displayedItem, _displayedItem.Confirmed ? "확인완료" : _displayedItem.AnalysisDisplay);
            }
            return true;
        }
        private void ClearWork(PdfItem item)
        {
            item.Parsed = null;
            item.SavedValues = null;
            item.SavedIngredients = null;
            item.Dirty = false;
            item.Confirmed = false;
            item.AnalysisStatus = "대기";
            item.AnalysisDisplay = "대기";
            item.Status = "대기";
        }
        private void SetAnalysisResult(PdfItem item, MsdsAnalysisResult result)
        {
            item.Parsed = result;
            item.AnalysisStatus = "분석완료";
            // 신뢰도 산식은 추후 검증: 현재는 실제 계산하지 않은 수치를 표시하지 않는다.
            item.AnalysisDisplay = "분석완료";
            item.Status = item.AnalysisDisplay;
        }

        private readonly ObservableCollection<PdfItem> _files = new();
        private readonly MsdsPdfTextExtractor _textExtractor = new();
        private readonly MsdsLocalFieldParser _fieldParser = new();
        private bool _isAnalyzing;
        private CancellationTokenSource? _analysisCts;
        private bool _previewZoomInitialized;

        public MsdsRegistrationView()
        {
            InitializeComponent();
            FilesGrid.ItemsSource = _files;
            UpdateCount();
            SizeChanged += (_, _) => UpdateResponsiveLayout();
            Loaded += (_, _) => { UpdateResponsiveLayout(); UpdateLogHeight(); UpdateNavigation(); };
            SizeChanged += (_, _) => UpdateLogHeight();
        }

        // null: 화면 너비에 따른 자동 상태, true/false: 사용자가 선택한 상태
        private bool? _filesOverride;
        private bool? _previewOverride;

        private void ToggleFiles_Click(object sender, RoutedEventArgs e)
        {
            _filesOverride = FilesPanel.Visibility != Visibility.Visible;
            UpdateResponsiveLayout();
        }

        private void TogglePreview_Click(object sender, RoutedEventArgs e)
        {
            _previewOverride = PreviewPanel.Visibility != Visibility.Visible;
            UpdateResponsiveLayout();
        }

        private bool _logExpanded = true;
        private bool _layoutUpdating;
        private int _lastLayoutMode = -1;
        private bool _lastFilesVisible;
        private bool _lastPreviewVisible;

        private void ToggleLog_Click(object sender, RoutedEventArgs e)
        {
            _logExpanded = !_logExpanded;
            UpdateLogHeight();
            LogList.Visibility = _logExpanded ? Visibility.Visible : Visibility.Collapsed;
            ToggleLogButton.Content = _logExpanded ? "로그 접기 ▲" : "로그 펼치기 ▼";
        }

        private void UpdateLogHeight()
        {
            if (LogRow == null || FilesPanel == null) return;
            // 목록 최소 높이를 확보하고 나머지 가용 공간 안에서 로그를 확장한다.
            double available = Math.Max(0, FilesPanel.ActualHeight - 11 * 2 - 43 - 36 - 32 - 38 - 34 - 35 - 110);
            LogRow.Height = new GridLength(_logExpanded ? Math.Min(270, available) : 0);
        }

        private void UpdateNavigation()
        {
            if (FilesGrid == null || FirstFileButton == null) return;
            int i = FilesGrid.SelectedItem is PdfItem item ? _files.IndexOf(item) : -1;
            CurrentFileText.Text = i < 0 ? "-----" : Path.GetFileNameWithoutExtension(_files[i].FileName)[..Math.Min(5, Path.GetFileNameWithoutExtension(_files[i].FileName).Length)];
            CurrentFileText.ToolTip = i < 0 ? "선택 파일 없음" : _files[i].FileName;
            FirstFileButton.IsEnabled = PreviousFileButton.IsEnabled = i > 0;
            NextFileButton.IsEnabled = LastFileButton.IsEnabled = i >= 0 && i < _files.Count - 1;
        }
        private void SelectFile(int index)
        {
            if (index < 0 || index >= _files.Count) return;
            FilesGrid.SelectedItem = _files[index];
            FilesGrid.ScrollIntoView(_files[index]);
            UpdateNavigation();
        }
        private void FirstFile_Click(object sender, RoutedEventArgs e) => SelectFile(0);
        private void PreviousFile_Click(object sender, RoutedEventArgs e) => SelectFile(FilesGrid.SelectedIndex - 1);
        private void NextFile_Click(object sender, RoutedEventArgs e) => SelectFile(FilesGrid.SelectedIndex + 1);
        private void LastFile_Click(object sender, RoutedEventArgs e) => SelectFile(_files.Count - 1);
        private void FileSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            string term = FileSearchBox.Text.Trim();
            if (term.Length == 0 || _files.Count == 0) return;
            int start = FilesGrid.SelectedIndex;
            for (int offset = 1; offset <= _files.Count; offset++)
            {
                int index = (start + offset + _files.Count) % _files.Count;
                if (_files[index].FileName.Contains(term, StringComparison.OrdinalIgnoreCase))
                { SelectFile(index); FileSearchBox.Dispatcher.BeginInvoke(new Action(() => FileSearchBox.Focus()), System.Windows.Threading.DispatcherPriority.Input); return; }
            }
        }
        private void SetAnalysisUi(bool analyzing)
        {
            _isAnalyzing = analyzing;
            FilesGrid.IsEnabled = true;
            FileSearchBox.IsEnabled = true;
            // 화면 내 데이터 수정 및 파일 추가/삭제를 모두 잠근다. 로그와 진행률은 유지한다.
            StopAnalysisButton.Visibility = analyzing ? Visibility.Visible : Visibility.Collapsed;
            StopAnalysisButton.IsHitTestVisible = true;
            foreach (var button in FindVisualChildren<System.Windows.Controls.Button>(FilesPanel))
                if (!ReferenceEquals(button, StopAnalysisButton) && !ReferenceEquals(button, ToggleLogButton))
                    button.IsEnabled = !analyzing;
            BottomClearButton.IsEnabled = !analyzing;
            RailAddButton.IsEnabled = !analyzing;
            foreach (var check in FindVisualChildren<System.Windows.Controls.CheckBox>(this)) check.IsEnabled = !analyzing;
            foreach (var box in FindVisualChildren<System.Windows.Controls.TextBox>(this))
                if (!ReferenceEquals(box, FileSearchBox) && !ReferenceEquals(box, FileNameBox))
                    box.IsReadOnly = analyzing;
            SaveCurrentButton.IsEnabled = !analyzing && _displayedItem?.Parsed != null;
            UpdateCount();
            UpdateNavigation();
        }
        private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T found) yield return found;
                foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
            }
        }
        private void StopAnalysis_Click(object sender, RoutedEventArgs e)
        {
            StopAnalysisButton.IsEnabled = false;
            _analysisCts?.Cancel();
            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  분석 중지 요청");
        }
        private void InitializePdfZoom()
        {
            if (_previewZoomInitialized) return;
            _previewZoomInitialized = true;
            PdfBrowser.ZoomFactorChanged += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateZoomText));
            UpdateZoomText();
        }
        private void UpdateZoomText()
        {
            if (ZoomText != null) ZoomText.Text = $"{PdfBrowser.ZoomFactor * 100:0}%";
        }
        private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(PdfBrowser.ZoomFactor * 1.1);
        private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(PdfBrowser.ZoomFactor / 1.1);
        private void SetZoom(double value)
        {
            PdfBrowser.ZoomFactor = Math.Clamp(value, 0.25, 5.0);
            UpdateZoomText();
        }

        private void UpdateResponsiveLayout()
        {
            if (_layoutUpdating || WorkspaceGrid == null || FilesPanel == null ||
                FilesRail == null || PreviewPanel == null) return;

            // ActualWidth는 레이아웃 결과에 따라 달라질 수 있으므로
            // 최상위 컨테이너의 가용 폭을 기준으로 모드를 결정한다.
            double width = ActualWidth - 16;
            if (width <= 0) return;
            int mode = width >= 1450 ? 2 : width >= 900 ? 1 : 0;
            bool showFiles = _filesOverride ?? mode == 2;
            bool showPreview = _previewOverride ?? mode >= 1;
            if (showFiles && width < 1250 && _previewOverride != true)
                showPreview = false;

            // 창 크기 변경 이벤트 중 반복해서 Star/Pixel 폭을 덮어쓰지 않는다.
            // 패널 표시 상태 또는 화면 구간이 바뀔 때만 재배치한다.
            if (_lastLayoutMode == mode && _lastFilesVisible == showFiles &&
                _lastPreviewVisible == showPreview) return;

            _layoutUpdating = true;
            try
            {
                _lastLayoutMode = mode;
                _lastFilesVisible = showFiles;
                _lastPreviewVisible = showPreview;
                FilesPanel.Visibility = showFiles ? Visibility.Visible : Visibility.Collapsed;
                FilesRail.Visibility = showFiles ? Visibility.Collapsed : Visibility.Visible;
                LeftSplitter.Visibility = showFiles ? Visibility.Visible : Visibility.Collapsed;
                PreviewPanel.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
                RightSplitter.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;

                var columns = WorkspaceGrid.ColumnDefinitions;
                // PDF 등록 영역은 고정 상한 폭을 사용하여 최대화 시 화면을 독점하지 않는다.
                columns[0].Width = new GridLength(showFiles ? (mode == 2 ? 340 : 300) : 44);
                columns[1].Width = new GridLength(showFiles ? 6 : 5);
                columns[3].Width = new GridLength(showPreview ? 6 : 0);
                columns[2].Width = new GridLength(showPreview ? (mode == 2 ? 42 : 60) : 1, GridUnitType.Star);
                columns[4].Width = showPreview
                    ? new GridLength(mode == 2 ? 32 : 40, GridUnitType.Star)
                    : new GridLength(0);

                ToggleFilesButton.Content = showFiles ? "◀  PDF 등록 접기" : "☰  PDF 등록";
                TogglePreviewButton.Content = showPreview ? "▣  원문 접기" : "▣  원문 보기";
            }
            finally { _layoutUpdating = false; }
        }

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "MSDS PDF 파일 선택",
                Filter = "PDF 파일 (*.pdf)|*.pdf",
                Multiselect = true
            };
            if (dialog.ShowDialog() == true) AddPaths(dialog.FileNames);
        }

        private void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "MSDS PDF 폴더 선택" };
            if (dialog.ShowDialog() != true) return;
            try { AddPaths(Directory.EnumerateFiles(dialog.FolderName, "*", SearchOption.TopDirectoryOnly)
                .Where(p => string.Equals(Path.GetExtension(p), ".pdf", StringComparison.OrdinalIgnoreCase))); }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "폴더 읽기 오류"); }
        }

        private void AddPaths(System.Collections.Generic.IEnumerable<string> paths)
        {
            int added = 0, duplicate = 0;
            HashSet<string> registeredNames;
            try { registeredNames = MsdsDbRegistration.GetRegisteredFileNames(); }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("DB 파일명 중복 확인에 실패하여 업로드를 중단합니다.\\n" + ex.Message,
                    "PDF 업로드 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            foreach (var path in paths)
            {
                if (!File.Exists(path)) continue;
                string name = Path.GetFileName(path);
                if (registeredNames.Contains(name) ||
                    _files.Any(x => string.Equals(x.FileName, name, StringComparison.OrdinalIgnoreCase)))
                {
                    duplicate++;
                    continue;
                }
                _files.Add(new PdfItem { Index = _files.Count + 1, FileName = name, FullPath = path });
                added++;
            }
            if (duplicate > 0)
                System.Windows.MessageBox.Show($"이미 등록되었거나 목록에 있는 동일 파일명 PDF {duplicate}건을 제외했습니다.",
                    "중복 파일명", MessageBoxButton.OK, MessageBoxImage.Information);
            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  PDF {added}건 추가, 중복 {duplicate}건 제외");
            UpdateCount();
            if (FilesGrid.SelectedItem == null && _files.Count > 0) FilesGrid.SelectedIndex = 0;
        }

        private async void FilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_revertingSelection) return;
            var incoming = FilesGrid.SelectedItem as PdfItem;
            if (!ReferenceEquals(_displayedItem, incoming) && !ConfirmLeave())
            {
                _revertingSelection = true;
                FilesGrid.SelectedItem = _displayedItem;
                _revertingSelection = false;
                UpdateNavigation();
                return;
            }
            UpdateNavigation();
            if (incoming is not PdfItem item) { _displayedItem = null; ResetPreview(); return; }
            _displayedItem = item;
            FileNameBox.Text = item.FileName;
            ShowExtractedFields(item);
            try
            {
                // WebView2는 비동기로 초기화된다. 초기화 완료 후 선택된 PDF를 연다.
                await PdfBrowser.EnsureCoreWebView2Async();
                InitializePdfZoom();
                // 초기화 도중 사용자가 다른 파일을 선택하거나 목록을 비울 수 있다.
                if (!ReferenceEquals(FilesGrid.SelectedItem, item)) return;
                PdfBrowser.CoreWebView2.Navigate(new Uri(Path.GetFullPath(item.FullPath)).AbsoluteUri);
            }
            catch (Exception ex)
            {
                LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  미리보기 실패: {ex.Message}");
            }
        }

        private void RemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (FilesGrid.SelectedItem is PdfItem item)
            {
                if (item.Status != "대기" && System.Windows.MessageBox.Show(
                    "선택한 PDF의 작업 내용을 삭제하시겠습니까?", "파일 제거 확인",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                _displayedItem = null;
                _files.Remove(item);
            }
            for (int i = 0; i < _files.Count; i++) _files[i].Index = i + 1;
            FilesGrid.Items.Refresh();
            UpdateCount();
            if (_files.Count == 0) ResetPreview();
            else if (FilesGrid.SelectedItem is PdfItem selected) ShowExtractedFields(selected);
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (_files.Any(f => f.Status != "대기") && System.Windows.MessageBox.Show(
                "목록과 지금까지의 작업 내용을 모두 삭제하시겠습니까?", "목록 초기화",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _displayedItem = null;
            _files.Clear();
            UpdateCount();
            ResetPreview();
            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  목록 초기화");
        }

        private void ResetPreview()
        {
            _loadingFields = true;
            FileNameBox.Clear();
            ProductNameBox.Clear();
            SupplierNameBox.Clear();
            IngredientsGrid.ItemsSource = null;
            ClearAdditionalFields();
            _loadingFields = false;
            SaveCurrentButton.IsEnabled = false;
            if (PdfBrowser.CoreWebView2 != null)
                PdfBrowser.CoreWebView2.Navigate("about:blank");
        }

        private static string GetMsdsAnalysisRule()
        {
            using var conn = new SqliteConnection(DatabaseManager.ActiveConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT zx_code_cmmt
                                FROM zx_code_mstr
                                WHERE zx_code_fldname = @fieldName
                                  AND COALESCE(zx_code_value, '') = ''";
            cmd.Parameters.AddWithValue("@fieldName", "MSDS_Analysis_rule");
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("MSDS_Analysis_rule 설정 레코드가 없습니다.");
            string rule = reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString()?.Trim() ?? "";
            if (reader.Read())
                throw new InvalidOperationException("MSDS_Analysis_rule 설정 레코드가 중복되었습니다.");
            if (rule != "1" && rule != "2" && rule != "3")
                throw new InvalidOperationException($"Comment 값이 올바르지 않습니다: '{rule}' (1, 2, 3만 허용)");
            return rule;
        }

        // 공통코드의 Comment 컬럼에서 AI 설정값을 조회한다.
        private static string GetAiSetting(string fieldName, bool required = true)
        {
            using var conn = new SqliteConnection(DatabaseManager.ActiveConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT zx_code_cmmt FROM zx_code_mstr
                                WHERE zx_code_fldname = @fieldName
                                  AND COALESCE(zx_code_value, '') = ''";
            cmd.Parameters.AddWithValue("@fieldName", fieldName);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                if (!required) return "";
                throw new InvalidOperationException(fieldName + " 설정 레코드가 없습니다.");
            }
            string value = reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString()?.Trim() ?? "";
            if (reader.Read()) throw new InvalidOperationException(fieldName + " 설정이 중복되었습니다.");
            if (required && string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(fieldName + "의 Comment 값이 비어 있습니다.");
            return value;
        }

        private bool PrepareNewAnalysis()
        {
            if (_files.Any(f => f.Status != "대기") && System.Windows.MessageBox.Show(
                "완료되지 않은 항목이 존재합니다.\n지금까지의 작업을 무시하고 처음부터 다시 시도하시겠습니까?",
                "재분석 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return false;
            foreach (var item in _files) ClearWork(item);
            if (FilesGrid.SelectedItem is PdfItem selected) ShowExtractedFields(selected);
            FilesGrid.Items.Refresh();
            return true;
        }
        private void FinishCancelledAnalysis()
        {
            if (_analysisCts?.IsCancellationRequested != true) return;
            foreach (var item in _files)
            {
                item.AnalysisStatus = "중지됨";
                item.Status = "중지됨";
            }
            FilesGrid.Items.Refresh();
        }

        // 등록된 PDF 전체에 대해 로컬 텍스트 추출 테스트를 실행한다.
        // 이 단계에서는 AI 분석, 필드 추출, DB 저장을 수행하지 않는다.
        private async void StartAnalysis_Click(object sender, RoutedEventArgs e)
        {
            if (_isAnalyzing || _files.Count == 0) return;
            // DB 설정에 따라 분석 모듈을 선택한다.
            string analysisRule;
            try { analysisRule = GetMsdsAnalysisRule(); }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("MSDS 분석 설정 조회 오류: " + ex.Message, "분석 중단");
                return;
            }

            // 1: AI Only - PDF 원본을 AI에 전달하고 기존 검토 화면에 표시한다.
            if (analysisRule == "1")
            {
                string apiKey, model;
                try
                {
                    apiKey = GetAiSetting("MSDS_API_KEY");
                    model = GetAiSetting("MSDS_AI_Model", required: false);
                    if (string.IsNullOrWhiteSpace(model)) model = "gpt-4.1-mini";
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show("AI 설정 조회 오류: " + ex.Message, "분석 중단");
                    return;
                }

                if (!PrepareNewAnalysis()) return;
                _analysisCts = new CancellationTokenSource();
                SetAnalysisUi(true);
                var aiTargets = _files.ToArray();
                int aiProcessed = 0;
                AnalysisProgressBar.Value = 0;
                AnalysisProgressText.Text = $"0 / {aiTargets.Length} (0%)";
                LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  AI PDF 분석 시작 ({aiTargets.Length}건, {model})");
                try
                {
                    var analyzer = new MsdsAiPdfAnalyzer(apiKey, model);
                    foreach (var item in aiTargets)
                    {
                        if (_analysisCts!.IsCancellationRequested) break;
                        item.Status = "분석 중";
                        FilesGrid.Items.Refresh();
                        try
                        {
                            var aiResult = await analyzer.AnalyzeAsync(item.FullPath, _analysisCts!.Token);
                            MsdsAiResultFiles.Save(item.FullPath, aiResult);
                            SetAnalysisResult(item, aiResult);
                            if (ReferenceEquals(FilesGrid.SelectedItem, item)) ShowExtractedFields(item);
                            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {item.FileName}: AI 추출 {aiResult.Fields.Count}필드, 성분 {aiResult.Ingredients.Count}건 (검증 필요)");
                        }
                        catch (OperationCanceledException) when (_analysisCts?.IsCancellationRequested == true)
                        {
                            item.Status = "중지됨";
                            break;
                        }
                        catch (Exception ex)
                        {
                            item.Status = "분석완료";
                            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {item.FileName}: AI 분석 오류 - {ex.Message}");
                        }
                        aiProcessed++;
                        int percent = (int)Math.Round(aiProcessed * 100.0 / aiTargets.Length);
                        AnalysisProgressBar.Value = percent;
                        AnalysisProgressText.Text = $"{aiProcessed} / {aiTargets.Length} ({percent}%)";
                        FilesGrid.Items.Refresh();
                    }
                    LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  AI PDF 분석 종료");
                }
                finally
                {
                    FinishCancelledAnalysis();
                    SetAnalysisUi(false);
                    _analysisCts?.Dispose();
                    _analysisCts = null;
                    StopAnalysisButton.IsEnabled = true;
                }
                return;
            }

            // 3: C# + AI는 연결 테스트 이후 구현한다.
            if (analysisRule == "3")
            {
                System.Windows.MessageBox.Show("C# + AI 통합 분석은 다음 단계에서 연결합니다.", "AI 모듈 연결 대기");
                return;
            }

            // 2: 기존 C# 분석 경로를 변경하지 않는다.
            // JSON 오류 시 기존 분석 결과를 변경하지 않는다.
            MsdsRuleSet rules;
            try { rules = MsdsRuleLoader.Load(); }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("파싱 규칙 오류: " + ex.Message, "JSON 규칙 확인");
                return;
            }
            if (!PrepareNewAnalysis()) return;
            _analysisCts = new CancellationTokenSource();
            SetAnalysisUi(true);
            var targets = _files.ToArray();
            int processed = 0;
            AnalysisProgressBar.Value = 0;
            AnalysisProgressText.Text = $"0 / {targets.Length} (0%)";
            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  PDF 텍스트 추출 시작 ({targets.Length}건)");
            try
            {
                foreach (var item in targets)
                {
                    if (_analysisCts!.IsCancellationRequested) break;
                    item.Status = "분석 중";
                    FilesGrid.Items.Refresh();
                    try
                    {
                        var result = await _textExtractor.ExtractAsync(item.FullPath, _analysisCts!.Token);
                        var parsed = _fieldParser.Parse(result, rules);
                        MsdsTempFiles.Save(item.FullPath, result, parsed);
                        SetAnalysisResult(item, parsed);
                        if (ReferenceEquals(FilesGrid.SelectedItem, item)) ShowExtractedFields(item);
                        if (result.HasExtractableText)
                            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {item.FileName}: 후보 필드 {item.Parsed.Fields.Count(f => f.Value != null)}건, CAS {item.Parsed.Ingredients.Count}건 (원문 확인 필요)");
                        string detail = result.HasExtractableText
                            ? $"{result.PageCount}페이지, 텍스트 {result.FullText.Length:N0}자"
                            : $"{result.PageCount}페이지, 텍스트 없음 (수동 입력 대상)";
                        LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {item.FileName}: {detail}");
                    }
                    catch (OperationCanceledException) when (_analysisCts?.IsCancellationRequested == true)
                    {
                        item.Status = "중지됨";
                        break;
                    }
                    catch (Exception ex)
                    {
                        item.Status = "분석완료";
                        LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {item.FileName}: 오류 - {ex.Message}");
                    }
                    processed++;
                    int percent = (int)Math.Round(processed * 100.0 / targets.Length);
                    AnalysisProgressBar.Value = percent;
                    AnalysisProgressText.Text = $"{processed} / {targets.Length} ({percent}%)";
                    FilesGrid.Items.Refresh();
                }
                LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  PDF 텍스트 추출 종료");
            }
            finally
            {
                FinishCancelledAnalysis();
                SetAnalysisUi(false);
                _analysisCts?.Dispose();
                _analysisCts = null;
                StopAnalysisButton.IsEnabled = true;
            }
        }

        // 파일별 추출 결과를 표시한다. DB 저장이나 자동 검증은 수행하지 않는다.
        private void ClearAdditionalFields()
        {
            foreach (var box in new[] { GracoCodeBox, RevisionDateBox, RevisionNumberBox,
                PhoneBox, AddressBox, UnNumberBox, HazardCodesBox, AutoIgnitionBox,
                VaporPressureBox, LelBox, UelBox, FlashPointBox,
                CorrosivityBox, PersonalProtectionBox }) box.Clear();
        }

        private void ShowExtractedFields(PdfItem item)
        {
            _loadingFields = true;
            string Get(string key) => item.Parsed?.Fields.FirstOrDefault(f => f.FieldKey == key)?.Value ?? "";
            ProductNameBox.Text = Get("msds_product_name");
            SupplierNameBox.Text = Get("msds_supplier_name");
            GracoCodeBox.Text = Get("msds_sds_code"); // SDS code는 Graco Code 후보: 사용자 검증 필요
            RevisionDateBox.Text = Get("msds_revision_date");
            RevisionNumberBox.Text = Get("msds_revision_number");
            PhoneBox.Text = Get("msds_supplier_phone");
            AddressBox.Text = Get("msds_supplier_address");
            UnNumberBox.Text = Get("msds_un_number");
            HazardCodesBox.Text = Get("msds_hazard_codes");
            AutoIgnitionBox.Text = Get("msds_autoignition");
            VaporPressureBox.Text = Get("msds_vapor_pressure");
            LelBox.Text = Get("msds_lel");
            UelBox.Text = Get("msds_uel");
            FlashPointBox.Text = Get("msds_flash_point");
            CorrosivityBox.Text = "";
            PersonalProtectionBox.Text = "";
            if (item.SavedValues != null)
                foreach (var entry in item.SavedValues)
                    if (FindName(entry.Key) is System.Windows.Controls.TextBox box) box.Text = entry.Value;
            IngredientsGrid.ItemsSource = item.SavedIngredients != null
                ? item.SavedIngredients.Select(v => new IngredientEdit
                  { Index = v.Index, Name = v.Name, CasNumber = v.CasNumber, ConcentrationRaw = v.ConcentrationRaw }).ToList()
                : item.Parsed?.Ingredients.Select((v, i) => new IngredientEdit
                  { Index = i + 1, Name = v.Name ?? "", CasNumber = v.CasNumber ?? "",
                    ConcentrationRaw = v.ConcentrationRaw ?? "" }).ToList();
            _loadingFields = false;
            SaveCurrentButton.IsEnabled = !_isAnalyzing && item.Parsed != null;
        }

        private void UpdateCount()
        {
            FileCountText.Text = $"등록된 파일 {_files.Count}건";
            StartAnalysisButton.IsEnabled = _files.Count > 0 && !_isAnalyzing;
            UpdateNavigation();
        }
    }
}
