using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

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
        }

        private readonly ObservableCollection<PdfItem> _files = new();

        public MsdsRegistrationView()
        {
            InitializeComponent();
            FilesGrid.ItemsSource = _files;
            UpdateCount();
            SizeChanged += (_, _) => UpdateResponsiveLayout();
            Loaded += (_, _) => UpdateResponsiveLayout();
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
            LogRow.Height = new GridLength(_logExpanded ? 90 : 0);
            LogList.Visibility = _logExpanded ? Visibility.Visible : Visibility.Collapsed;
            ToggleLogButton.Content = _logExpanded ? "로그 접기 ▲" : "로그 펼치기 ▼";
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
                columns[2].Width = new GridLength(1, GridUnitType.Star);
                columns[4].Width = showPreview
                    ? new GridLength(mode == 2 ? 480 : 420)
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
            int added = 0;
            foreach (var path in paths)
            {
                if (!File.Exists(path) || _files.Any(x => string.Equals(x.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                _files.Add(new PdfItem { Index = _files.Count + 1, FileName = Path.GetFileName(path), FullPath = path });
                added++;
            }
            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  PDF {added}건 추가");
            UpdateCount();
            if (FilesGrid.SelectedItem == null && _files.Count > 0) FilesGrid.SelectedIndex = 0;
        }

        private void FilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FilesGrid.SelectedItem is not PdfItem item) return;
            FileNameBox.Text = item.FileName;
            try { PdfBrowser.Navigate(new Uri(item.FullPath)); }
            catch (Exception ex) { LogList.Items.Insert(0, $"미리보기 실패: {ex.Message}"); }
        }

        private void RemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (FilesGrid.SelectedItem is PdfItem item) _files.Remove(item);
            for (int i = 0; i < _files.Count; i++) _files[i].Index = i + 1;
            FilesGrid.Items.Refresh();
            UpdateCount();
            if (_files.Count == 0) ResetPreview();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _files.Clear();
            UpdateCount();
            ResetPreview();
            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  목록 초기화");
        }

        private void ResetPreview()
        {
            FileNameBox.Clear();
            PdfBrowser.Navigate("about:blank");
        }

        // 파일 등록과 분석 실행은 분리한다. 분석 엔진 연결 전에는 실행 안내만 표시한다.
        private void StartAnalysis_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.MessageBox.Show(
                $"분석 대상 PDF {_files.Count}건이 등록되었습니다.\nPDF 분석 엔진은 아직 연결되지 않았습니다.",
                "MSDS 분석 시작", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void UpdateCount()
        {
            FileCountText.Text = $"등록된 파일 {_files.Count}건";
            StartAnalysisButton.IsEnabled = _files.Count > 0;
        }
    }
}
