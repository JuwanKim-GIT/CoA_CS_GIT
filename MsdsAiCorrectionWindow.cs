using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using QCS.Analysis;
using WpfMessageBox = System.Windows.MessageBox;
using WpfButton = System.Windows.Controls.Button;
using DataGrid = System.Windows.Controls.DataGrid;
using Binding = System.Windows.Data.Binding;
using RichTextBox = System.Windows.Controls.RichTextBox;
using System.Windows.Documents;
using System.ComponentModel;
using WrapPanel = System.Windows.Controls.WrapPanel;
using DataGridSelectionMode = System.Windows.Controls.DataGridSelectionMode;
using Microsoft.Web.WebView2.Wpf;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace QCS
{
    // PDF 한 건의 필드 교정: 추천 후보, 전체 원문 검색, 직접 입력.
    public sealed class MsdsAiCorrectionWindow : Window
    {
        private sealed class FieldRow : System.ComponentModel.INotifyPropertyChanged
        {
            public string Key { get; init; } = "";
            public string Group { get; init; } = "기본정보";
            public string Name { get; init; } = "";
            public string Original { get; init; } = "";
            public string Extracted { get; init; } = "";
            private string _value = "";
            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
            public string Value { get => _value; set { _value=value; foreach(var name in new[]{nameof(Value),nameof(DisplayConfidence),nameof(Status)}) PropertyChanged?.Invoke(this,new System.ComponentModel.PropertyChangedEventArgs(name)); } }
            public Candidate? Evidence { get; set; }
            public bool Approved { get; set; }
            public string Confidence { get; init; } = "미산정";
            public string DisplayConfidence => Value != Original ? "사용자 확인" : Confidence;
            public string Status => string.IsNullOrWhiteSpace(Value) ? "누락" :
                Value != Original ? "수정" : "유지";
        }
        private sealed class Candidate
        {
            public string Value { get; init; } = "";
            public string Source { get; init; } = "";
            public int Page { get; init; }
            public int Rank { get; init; }
            public string Label { get; init; } = "미확인";
            public string Context { get; init; } = "";
        }
        private static readonly Dictionary<string, string> Names = new()
        {
            ["ProductNameBox"]="제품명", ["SupplierNameBox"]="공급업체", ["GracoCodeBox"]="GRACO 코드",
            ["RevisionDateBox"]="개정일", ["RevisionNumberBox"]="개정번호", ["PhoneBox"]="전화번호",
            ["AddressBox"]="주소", ["UnNumberBox"]="UN 번호", ["HazardCodesBox"]="유해위험 문구",
            ["AutoIgnitionBox"]="자연발화온도", ["VaporPressureBox"]="증기압", ["LelBox"]="폭발하한",
            ["UelBox"]="폭발상한", ["FlashPointBox"]="인화점", ["CorrosivityBox"]="피부 부식성/자극성",
            ["PersonalProtectionBox"]="보호구", ["MoelSubmissionBox"]="제출번호", ["MolecularFormulaBox"]="분자식", ["PhysicalStateBox"]="물리적 상태", ["HazardousGoodsBox"]="위험물 분류", ["ReactivityBox"]="반응성"
        };
        private static readonly Dictionary<string, string[]> Hints = new()
        {
            ["ProductNameBox"] = new[]{"제품명","product name","product identifier"},
            ["SupplierNameBox"] = new[]{"공급자","제조사","supplier","manufacturer","회사명","company name"},
            ["RevisionDateBox"] = new[]{"개정일","revision date","revised"},
            ["RevisionNumberBox"] = new[]{"개정번호","revision no","version"},
            ["PhoneBox"] = new[]{"전화번호","telephone","phone","tel"},
            ["AddressBox"] = new[]{"주소","address"},
            ["UnNumberBox"] = new[]{"un number","un no","유엔번호","un "},
            ["HazardCodesBox"] = new[]{"유해","hazard","h-code"},
            ["AutoIgnitionBox"] = new[]{"자연발화","auto-ignition","autoignition"},
            ["VaporPressureBox"] = new[]{"증기압","vapor pressure"},
            ["LelBox"] = new[]{"폭발하한","lower explosive","lel"},
            ["UelBox"] = new[]{"폭발상한","upper explosive","uel"},
            ["FlashPointBox"] = new[]{"인화점","flash point"},
            ["CorrosivityBox"] = new[]{"피부 자극성","피부 부식성","corrosive"},
            ["PersonalProtectionBox"] = new[]{"보호구","personal protection","ppe","보호장갑","보안경","보호의","P280","protective gloves"},
            ["MoelSubmissionBox"] = new[]{"제출번호","제품 등록 번호","제품 등록번호","submission number"}, ["MolecularFormulaBox"] = new[]{"분자식","molecular formula"}, ["PhysicalStateBox"] = new[]{"물리적 상태","physical state"}, ["HazardousGoodsBox"] = new[]{"위험물 분류","hazardous goods class"}, ["ReactivityBox"] = new[]{"반응성","reactivity"}
        };
        private readonly string _fileName;
        private readonly string _filePath;
        private readonly MsdsPdfTextResult _pdf;
        private readonly MsdsAnalysisResult _analysis;
        private readonly ObservableCollection<FieldRow> _rows = new();
        private readonly DataGrid _fields = new();
        private readonly DataGrid _candidates = new();
        private readonly TextBox _search = new();
        private readonly TextBox _fieldSearch = new();
        private ICollectionView? _fieldView;
        private string _appliedFieldQuery = "";
        private readonly TextBox _editor = new();
        private string _highlightSource = "";
        private string _fallbackText = "";
        private WpfButton? _fallbackButton;
        private readonly WebView2 _viewer = new();
        private readonly TextBlock _pdfStatus = new();
        private int _page = 1;
        private bool _closed;
        private readonly TextBlock _original = new();
        private readonly TextBlock _notice = new();
        private readonly CheckBox _more = new() { Content = "추가 후보 보기 (전체 파싱 원문)", Margin = new Thickness(0,8,0,5) };
        private bool _changing;
        private readonly CheckBox _approve = new() { Content = "원문과 최종값 검증 완료 (동일 PDF 학습 허용)" };
        private readonly List<string> _previewFiles = new();
        public Dictionary<string, string> CorrectedValues { get; } = new();
        internal List<MsdsLearningEntry> PendingLearning { get; } = new();

        public MsdsAiCorrectionWindow(string fileName, string filePath, MsdsPdfTextResult pdf,
            MsdsAnalysisResult analysis, Dictionary<string,string> currentValues, IEnumerable<string> keys,
            IReadOnlyDictionary<string,string>? originalAiValues = null)
        {
            _fileName = fileName; _filePath = filePath; _pdf = pdf; _analysis = analysis;
            Title = "QCS - AI 분석 교정: " + fileName;
            Width = 1380; Height = 830; MinWidth = 1000; MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brushes.White;
            foreach (var key in keys)
            {
                var value = currentValues.TryGetValue(key, out var v) ? v : "";
                _rows.Add(new FieldRow { Key = key, Name = Names.TryGetValue(key,out var n) ? n : MsdsIngredientLearningKey.Parse(key,out var cas,out var field) ? IngredientFieldName(field) : key,
                    Group = MsdsIngredientLearningKey.Parse(key,out var groupCas,out _) ? IngredientGroup(groupCas) : "기본정보",
                    Original = value, Extracted = OriginalAiValue(originalAiValues, analysis, key, value), Value = value, Confidence = ConfidenceText(analysis, key, value) });
            }
            BuildUi();
            Loaded += async (_,__) => { try { await _viewer.EnsureCoreWebView2Async(); if (!_closed) NavigatePdf(); } catch(Exception ex) { if (!_closed) _pdfStatus.Text = "PDF 뷰어 초기화 실패: " + ex.Message; } };
            Closed += (_,__) => { _closed = true; _viewer.Dispose(); foreach(var path in _previewFiles) try { System.IO.File.Delete(path); } catch { } };
            _fieldView = CollectionViewSource.GetDefaultView(_rows);
            _fieldView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FieldRow.Group)));
            _fieldView.Filter = item => item is FieldRow row && (string.IsNullOrEmpty(_appliedFieldQuery)
                || row.Group.Contains(_appliedFieldQuery,StringComparison.OrdinalIgnoreCase)
                || row.Name.Contains(_appliedFieldQuery,StringComparison.OrdinalIgnoreCase)
                || row.Value.Contains(_appliedFieldQuery,StringComparison.OrdinalIgnoreCase));
            _fields.ItemsSource = _fieldView;
            _fields.SelectedIndex = 0;
        }
        private static string OriginalAiValue(IReadOnlyDictionary<string,string>? original,
            MsdsAnalysisResult analysis, string key, string fallback)
        {
            if (original != null)
            {
                if (original.TryGetValue(key, out var ingredientValue)) return ingredientValue;
                var field = analysis.Fields.FirstOrDefault(f => MapField(f.FieldKey) == key);
                if (field != null && original.TryGetValue(field.FieldKey, out var originalValue)) return originalValue;
            }
            return ExtractedValue(analysis, key, fallback);
        }
        private static string ExtractedValue(MsdsAnalysisResult analysis, string key, string fallback)
        {
            if (MsdsIngredientLearningKey.Parse(key, out var cas, out var property))
            {
                var matches = analysis.Ingredients.Where(x => x.CasNumber?.Trim() == cas).ToList();
                return matches.Count == 1 ? MsdsIngredientLearningKey.Get(matches[0], property) ?? "" : fallback;
            }
            var field = analysis.Fields.FirstOrDefault(f => MapField(f.FieldKey) == key);
            return field?.Value ?? fallback;
        }
        private void ExpandMatchingGroups(DependencyObject parent)
        {
            for (int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            {
                var child=VisualTreeHelper.GetChild(parent,i);
                if (child is Expander expander && !string.IsNullOrWhiteSpace(_appliedFieldQuery)) expander.IsExpanded=true;
                ExpandMatchingGroups(child);
            }
        }
        private static string IngredientFieldName(string field) => field switch
        {
            "Twa" => "TWA", "Stel" => "STEL", "OralLd50" => "경구 LD50",
            "DermalLd50" => "경피 LD50", "InhalationLc50" => "흡입 LC50", _ => field
        };
        private string IngredientGroup(string cas)
        {
            var name = _analysis.Ingredients.FirstOrDefault(x => x.CasNumber?.Trim() == cas)?.Name;
            return string.IsNullOrWhiteSpace(name) ? $"구성성분 ({cas})" : $"{name} ({cas})";
        }
        private sealed class GroupExpandConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
                => string.Equals(value as string,"기본정보",StringComparison.Ordinal) || !string.IsNullOrWhiteSpace(parameter as string);
            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
                => Binding.DoNothing;
        }
        private void ConfigureFieldGroups()
        {
            var group = new GroupStyle();
            var template = new ControlTemplate(typeof(GroupItem));
            var expander = new FrameworkElementFactory(typeof(Expander));
            expander.SetBinding(HeaderedContentControl.HeaderProperty, new Binding("Name") { Mode = BindingMode.OneWay });
            expander.SetBinding(Expander.IsExpandedProperty, new Binding("Name") { Mode = BindingMode.OneWay, Converter=new GroupExpandConverter() });
            expander.SetValue(FrameworkElement.MarginProperty, new Thickness(0,3,0,0));
            var presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
            expander.AppendChild(presenter);
            template.VisualTree = expander;
            group.ContainerStyle = new System.Windows.Style(typeof(GroupItem));
            group.ContainerStyle.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, template));
            _fields.GroupStyle.Add(group);
        }
        private static string ConfidenceText(MsdsAnalysisResult analysis, string key, string value)
        {
            var field = analysis.Fields.FirstOrDefault(f => MapField(f.FieldKey) == key);
            if (field?.Status == MsdsFieldStatus.ManuallyEntered) return "학습 반영";
            if (field?.Confidence is double c) return c.ToString("P0");
            if (string.IsNullOrWhiteSpace(value)) return "미추출";
            return field == null ? "미산정" : "확인 필요";
        }
        private static TextBlock Text(string text, int size = 13, bool bold = false) => new()
        {
            Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(Color.FromRgb(30,58,95)), Margin = new Thickness(0,5,0,6),
            TextWrapping = TextWrapping.Wrap
        };
        private static WpfButton Button(string label, RoutedEventHandler click, bool primary = false)
        {
            var button = new WpfButton { Content = label, MinWidth = 115, Height = 36,
                Padding = new Thickness(12,4,12,4), Margin = new Thickness(5,0,0,0) };
            button.SetResourceReference(StyleProperty, primary ? "QcsPrimaryButton" : "QcsActionButton");
            button.Click += click;
            return button;
        }
        private void BuildUi()
        {
            var root = new DockPanel { Margin = new Thickness(14) };
            Content = root;
            var footer = new DockPanel { Height = 55, LastChildFill = true };
            DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(Button("교정값 적용", Apply, true));
            actions.Children.Add(Button("취소", (s,e)=>Close()));
            DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions);
            _notice.VerticalAlignment = VerticalAlignment.Center;
            _notice.Text = "";
            _notice.Foreground = Brushes.DimGray;
            footer.Children.Add(_notice);
            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3,GridUnitType.Star), MinWidth=240 });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3,GridUnitType.Star), MinWidth=260 });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4,GridUnitType.Star), MinWidth=300 });
            root.Children.Add(columns);
            var left = new DockPanel { Margin = new Thickness(0,0,8,0) };
            Grid.SetColumn(left,0); columns.Children.Add(left);
            var heading = Text("분석 항목 (문서 전체)",15,true);
            DockPanel.SetDock(heading,Dock.Top);left.Children.Add(heading);
            var fieldSearchPanel = new DockPanel { Margin = new Thickness(0,0,0,6) };
            _fieldSearch.Height=30;
            _fieldSearch.ToolTip="항목명, 성분명, CAS No., 추출값 입력 후 Enter";
            _fieldSearch.KeyDown += (_,e) => { if (e.Key == System.Windows.Input.Key.Enter)
                { _appliedFieldQuery=_fieldSearch.Text.Trim(); _fieldView?.Refresh();
                    _fields.Dispatcher.BeginInvoke(new Action(() => ExpandMatchingGroups(_fields)),System.Windows.Threading.DispatcherPriority.Loaded);
                    e.Handled=true; } };
            DockPanel.SetDock(fieldSearchPanel,Dock.Top); left.Children.Add(fieldSearchPanel);
            fieldSearchPanel.Children.Add(_fieldSearch);
            ConfigureGrid(_fields, ("분석 항목","Name",1), ("현재 추출값","Value",2.5));
            ConfigureFieldGroups();
            _fields.SelectionChanged += (_,__) => ShowField();
            left.Children.Add(_fields);
            AddSplitter(columns,1);AddSplitter(columns,3);
            var center = new DockPanel { Margin = new Thickness(12,0,12,0) };
            Grid.SetColumn(center,2);columns.Children.Add(center);
            var title = Text("교정값 검색 및 선택",15,true);
            DockPanel.SetDock(title,Dock.Top);center.Children.Add(title);
            var centerGrid = new Grid();
            centerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            centerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            centerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1,GridUnitType.Star), MinHeight=110 });
            centerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            centerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            centerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            centerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            center.Children.Add(centerGrid);
            var originalPanel = new StackPanel(); Grid.SetRow(originalPanel,0);centerGrid.Children.Add(originalPanel);
            originalPanel.Children.Add(Text("최초 AI 추출값 (학습 적용 전)",13,true));
            _original.TextWrapping=TextWrapping.Wrap;
            _original.MaxHeight=68;
            _original.Margin=new Thickness(0,0,0,4);
            originalPanel.Children.Add(_original);
            var candidateLabel=Text("원문 근거 / 추천 후보",13,true);
            Grid.SetRow(candidateLabel,1);centerGrid.Children.Add(candidateLabel);
            ConfigureGrid(_candidates, ("원문 항목명","Label",1), ("후보값","Value",2), ("페이지","Page",0.65));
            Grid.SetRow(_candidates,2);centerGrid.Children.Add(_candidates);
            _candidates.SelectionChanged += (_,__) =>
            {
                if (_changing || _candidates.SelectedItem is not Candidate candidate) return;
                if (_fields.SelectedItem is FieldRow selected) { selected.Evidence = candidate; selected.Approved=false; _approve.IsChecked=false; }
                _editor.Text = candidate.Value;
                ShowEvidence(candidate.Page, candidate.Source);
            };
            _more.Content="추가 후보 보기";
            _more.Margin=new Thickness(0,5,0,3);
            _more.Checked += (_,__)=>RefreshCandidates();
            _more.Unchecked += (_,__)=>RefreshCandidates();
            Grid.SetRow(_more,3);centerGrid.Children.Add(_more);
            var searchPanel=new DockPanel { Margin=new Thickness(0,2,0,5) };
            var searchLabel=Text("검색",13,true);searchLabel.Width=65;
            DockPanel.SetDock(searchLabel,Dock.Left);searchPanel.Children.Add(searchLabel);
            _search.Height=32;_search.TextChanged += (_,__)=>RefreshCandidates();searchPanel.Children.Add(_search);
            Grid.SetRow(searchPanel,4);centerGrid.Children.Add(searchPanel);
            var editorPanel=new DockPanel { Margin=new Thickness(0,2,0,5) };
            var editorLabel=Text("교정값",13,true);editorLabel.Width=65;
            DockPanel.SetDock(editorLabel,Dock.Left);editorPanel.Children.Add(editorLabel);
            _editor.Height=62;_editor.AcceptsReturn=true;_editor.TextWrapping=TextWrapping.Wrap;
            _editor.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
            _editor.TextChanged += (_,__) =>
            {
                if (_changing || _fields.SelectedItem is not FieldRow row) return;
                row.Value=_editor.Text;row.Approved=false;_approve.IsChecked=false;
            };
            editorPanel.Children.Add(_editor);
            Grid.SetRow(editorPanel,5);centerGrid.Children.Add(editorPanel);
            _approve.Margin=new Thickness(0,4,0,3);
            _approve.Checked += (_,__) =>
            {
                if (_changing || _fields.SelectedItem is not FieldRow row) return;
                if (row.Key=="GracoCodeBox") { _approve.IsChecked=false;return; }
                // 후보 선택은 필수가 아닙니다. 공란 삭제 교정도 사용자 검증을 허용합니다.
                row.Approved=true;
            };
            _approve.Unchecked += (_,__) => { if (!_changing && _fields.SelectedItem is FieldRow r) r.Approved=false; };
            Grid.SetRow(_approve,6);centerGrid.Children.Add(_approve);
            var right = new DockPanel { Margin = new Thickness(10,0,0,0) };
            Grid.SetColumn(right,4);columns.Children.Add(right);
            var rightTitle=Text("PDF 원본 / 근거 확인",15,true);
            var header = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(header,Dock.Top); right.Children.Add(header);
            var open = Button("원본 PDF 열기",(s,e)=>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_filePath) { UseShellExecute=true }); }
                catch(Exception ex) { WpfMessageBox.Show("PDF 열기 실패: " + ex.Message); }
            });
            open.MinWidth=0; open.Height=28; open.Padding=new Thickness(8,2,8,2); open.FontSize=11;
            DockPanel.SetDock(open,Dock.Right); header.Children.Add(open); header.Children.Add(rightTitle);
            _fallbackButton=Button("원문 근거 확인", (_,__) => WpfMessageBox.Show(_fallbackText,"원문 근거"));
            _fallbackButton.Visibility=Visibility.Collapsed;
            DockPanel.SetDock(_fallbackButton,Dock.Top); right.Children.Add(_fallbackButton);
            _pdfStatus.TextWrapping=TextWrapping.Wrap;
            DockPanel.SetDock(_pdfStatus,Dock.Top); right.Children.Add(_pdfStatus);
            right.Children.Add(_viewer);
        }
        private void NavigatePdf()
        {
            try {
                if (_viewer.CoreWebView2 == null) return;
                var path=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"QCS-correction-"+Guid.NewGuid().ToString("N")+".pdf");
                _previewFiles.Add(path);
                bool marked=false;
                if (!string.IsNullOrWhiteSpace(_highlightSource))
                {
                    try { marked=MsdsPdfHighlightPreview.TryCreate(_filePath,path,_page,_highlightSource); }
                    catch { marked=false; }
                }
                if (!marked) System.IO.File.Copy(_filePath,path,true);
                _pdfStatus.Text=(!marked && _highlightSource.Length>0)?"PDF에서 원문 위치를 확정하지 못했습니다.":"";
                if (_fallbackButton!=null) _fallbackButton.Visibility=(!marked && _fallbackText.Length>0)?Visibility.Visible:Visibility.Collapsed;
                _viewer.CoreWebView2.Navigate(new Uri(path).AbsoluteUri + "#page=" + _page);
            }
            catch(Exception ex) { _pdfStatus.Text = "PDF 이동 실패: " + ex.Message; if(_fallbackButton!=null && _fallbackText.Length>0) _fallbackButton.Visibility=Visibility.Visible; }
        }
        private void ConfigureGrid(DataGrid grid, params (string Header,string Path,double Weight)[] columns)
        {
            grid.AutoGenerateColumns=false; grid.IsReadOnly=true; grid.CanUserAddRows=false;
            grid.SelectionMode=DataGridSelectionMode.Single; grid.RowHeight=double.NaN; grid.MinRowHeight=30; grid.SelectionUnit=System.Windows.Controls.DataGridSelectionUnit.FullRow;
            grid.SetResourceReference(StyleProperty,"QcsGrid");
            var rowStyle=new System.Windows.Style(typeof(System.Windows.Controls.DataGridRow), TryFindResource("QcsGridRow") as System.Windows.Style);
            var selected=new Trigger { Property=System.Windows.Controls.DataGridRow.IsSelectedProperty, Value=true };
            selected.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(0,120,212))));
            selected.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty,Brushes.White));
            rowStyle.Triggers.Add(selected); grid.RowStyle=rowStyle;
            var cellStyle=new System.Windows.Style(typeof(System.Windows.Controls.DataGridCell), TryFindResource(typeof(System.Windows.Controls.DataGridCell)) as System.Windows.Style);
            var cellSelected=new Trigger {Property=System.Windows.Controls.DataGridCell.IsSelectedProperty,Value=true};
            cellSelected.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(0,120,212))));
            cellSelected.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty,Brushes.White));
            cellStyle.Triggers.Add(cellSelected); grid.CellStyle=cellStyle;
            foreach(var c in columns)
            {
                var style = new System.Windows.Style(typeof(TextBlock),TryFindResource("QcsGridText") as System.Windows.Style);
                style.Setters.Add(new Setter(TextBlock.TextWrappingProperty,TextWrapping.Wrap));
                style.Setters.Add(new Setter(System.Windows.FrameworkElement.ToolTipProperty,new Binding(c.Path)));
                grid.Columns.Add(new DataGridTextColumn { Header=c.Header, Binding=new Binding(c.Path), Width=new DataGridLength(c.Weight,DataGridLengthUnitType.Star), ElementStyle=style });
            }
        }
        private void EvidenceText(string value) { _pdfStatus.Text=value; _highlightSource=""; _fallbackText=""; if(_fallbackButton!=null) _fallbackButton.Visibility=Visibility.Collapsed; }
        private static void AddSplitter(Grid grid,int col)
        {
            var splitter = new GridSplitter { ResizeBehavior=GridResizeBehavior.PreviousAndNext, Width=5,Background=new SolidColorBrush(Color.FromRgb(226,234,245)),
                HorizontalAlignment=HorizontalAlignment.Stretch };
            Grid.SetColumn(splitter,col);grid.Children.Add(splitter);
        }
        private void ShowField()
        {
            if (_fields.SelectedItem is not FieldRow row) return;
            _changing=true;
            _original.Text=string.IsNullOrWhiteSpace(row.Extracted)?"(추출값 없음)":row.Extracted;
            _editor.Text=row.Value; _approve.IsChecked=row.Approved; _approve.IsEnabled=row.Key!="GracoCodeBox";
            _search.Text="";
            _more.IsChecked=false;
            _changing=false;
            RefreshCandidates();

        }
        private void RefreshCandidates()
        {
            if (_changing || _fields.SelectedItem is not FieldRow row) return;
            if (row.Key=="GracoCodeBox") { _candidates.ItemsSource=null; EvidenceText("GRACO 코드는 파일명 기준입니다. 직접 수정만 가능합니다."); return; }
            var hints=Hints.TryGetValue(row.Key,out var h)?h:Array.Empty<string>();
            if (MsdsIngredientLearningKey.Parse(row.Key, out var cas, out var field))
                hints = field switch { "Twa" => new[]{"TWA"}, "Stel" => new[]{"STEL"}, "OralLd50" => new[]{"경구", "oral"}, "DermalLd50" => new[]{"경피", "dermal"}, _ => new[]{"흡입", "inhalation"} };
            var query=_search.Text.Trim();
            var result=new List<Candidate>();
            var existing=_analysis.Fields.FirstOrDefault(f=>MapField(f.FieldKey)==row.Key);
            if (!string.IsNullOrWhiteSpace(existing?.Value))
                result.Add(new Candidate { Value=existing.Value!,Source=existing.SourceText??"",Page=existing.SourcePage??0,Rank=100, Label="미확인", Context=existing.SourceText??"" });
            foreach (var page in _pdf.Pages)
            {
                var lines=page.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries);
                for (int i=0;i<lines.Length;i++)
                {
                    var line=lines[i].Trim();
                    if (line.Length<2)continue;
                    var matched=hints.Any(x=>line.Contains(x,StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(query) && !line.Contains(query,StringComparison.OrdinalIgnoreCase))continue;
                    if (string.IsNullOrEmpty(query) && !matched && _more.IsChecked!=true)continue;
                    string candidate=line;
                    if (matched)
                    {
                        var cut=line.IndexOf(':');
                        if (cut<0)cut=line.IndexOf('：');
                        if (cut>=0 && cut<line.Length-1)candidate=line[(cut+1)..].Trim();
                        if (candidate.Length<2 && i+1<lines.Length)candidate=lines[i+1].Trim();
                    }
                    if (candidate.Length>220)candidate=candidate[..220];
                    if (string.IsNullOrWhiteSpace(candidate))continue;
                    result.Add(new Candidate {Value=candidate,Source=line,Page=page.PageNumber,Rank=matched?70:20, Label=matched && (line.IndexOf(':') > 0 || line.IndexOf('：') > 0) ? line.Split(new[]{':','：'},2)[0].Trim() : "미확인", Context=string.Join("\n",lines.Skip(Math.Max(0,i-2)).Take(Math.Min(5,lines.Length-Math.Max(0,i-2))))});
                    if (result.Count>=400)break;
                }
                if (result.Count>=400)break;
            }
            var sorted=result.OrderByDescending(c=>c.Rank).ThenBy(c=>c.Page)
                .GroupBy(c=>c.Value,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).Take(150).ToList();
            if (row.Evidence is Candidate saved && !sorted.Any(c=>c.Value==saved.Value && c.Source==saved.Source && c.Page==saved.Page) && query.Length==0) sorted.Insert(0,saved);
            if (string.IsNullOrEmpty(query) && _more.IsChecked!=true)
                sorted=sorted.Where(c=>c==row.Evidence || IsRecommended(row,c)).Take(5).ToList();
            var match = sorted.FirstOrDefault(c=>row.Evidence!=null && c.Value==row.Evidence.Value && c.Source==row.Evidence.Source && c.Page==row.Evidence.Page)
                ?? sorted.FirstOrDefault(c=>!string.IsNullOrWhiteSpace(row.Value) && Normalize(c.Value)==Normalize(row.Value));
            _changing=true;
            _candidates.ItemsSource=sorted;
            _candidates.SelectedItem=match;
            _changing=false;
            if(match!=null) { if(row.Evidence==null && row.Value==row.Original) row.Evidence=match; ShowEvidence(match.Page,match.Source); }
            else { EvidenceText(sorted.Count==0?"추천 후보 없음. 추가 후보 또는 원문 검색으로 확인하세요.":"후보를 선택하면 원문을 표시합니다."); NavigatePdf(); }
        }
        private static string Normalize(string value) => Regex.Replace(value, @"\s+", "").Trim().ToUpperInvariant();
        private static bool IsRecommended(FieldRow row,Candidate c)
        {
            var v=c.Value.Trim();
            if(v.Length==0) return false;
            if(c.Rank==100) return true;
            if (MsdsIngredientLearningKey.Parse(row.Key, out _, out _)) return false; // existing analysis is retained for inspection, not re-approved
            if(c.Page<=0 || v.EndsWith(":") || v.EndsWith("：") || Normalize(v)==Normalize(c.Label)) return false;
            bool labelled=c.Label!="미확인" && Normalize(c.Label)!=Normalize(v);
            return row.Key switch {
                "SupplierNameBox" => c.Page<=2 && labelled && Regex.IsMatch(v,@"㈜|\(주\)|주식회사|\b(?:Ltd|Inc|GmbH|Corporation|LLC|Co\.)\b",RegexOptions.IgnoreCase),
                "ProductNameBox" => c.Page<=2 && labelled,
                "AddressBox" => c.Page<=2 && labelled && v.Length>=8,
                "PhoneBox" => c.Page<=2 && labelled && Regex.IsMatch(v,@"\d[\d ()+.-]{5,}\d"),
                "RevisionDateBox" => labelled && Regex.IsMatch(v,@"\d{4}[-./년 ]\s*\d{1,2}[-./월 ]\s*\d{1,2}"),
                "RevisionNumberBox" => labelled && Regex.IsMatch(v,@"^\d+(?:\.\d+)*(?:/[A-Za-z]+)?$"),
                "UnNumberBox" => Regex.IsMatch(v,@"^(?:UN\s*)?\d{4}$",RegexOptions.IgnoreCase),
                "HazardCodesBox" => Regex.IsMatch(v,@"\bH\d{3}\b",RegexOptions.IgnoreCase),
                "PersonalProtectionBox" => Regex.IsMatch(v,@"보호장갑|보안경|안면보호구|보호의|protective gloves|eye protection|face protection",RegexOptions.IgnoreCase) && !v.EndsWith(":"),
                "CorrosivityBox" => Regex.IsMatch(v,@"부식성|corrosive|corrosion",RegexOptions.IgnoreCase) && Regex.IsMatch(v,@"구분|category|보임|causes|부식성을",RegexOptions.IgnoreCase),
                "LelBox" or "UelBox" => labelled && Regex.IsMatch(v,@"^(?:[<>≤≥~–-]\s*)?\d+(?:[.,]\d+)?\s*(?:%|vol\.?\s*%)?$",RegexOptions.IgnoreCase),
                "FlashPointBox" or "AutoIgnitionBox" => labelled && Regex.IsMatch(v,@"\d+\s*(?:°\s*[CF]|℃|℉)",RegexOptions.IgnoreCase),
                "VaporPressureBox" => labelled && Regex.IsMatch(v,@"\d+\s*(?:hPa|kPa|Pa|mmHg|mbar)",RegexOptions.IgnoreCase),
                "MoelSubmissionBox" => labelled && Regex.IsMatch(v,@"[A-Z0-9-]{5,}",RegexOptions.IgnoreCase),
                _ => false
            };
        }
        private static string MapField(string field) => field switch
        {
            "msds_product_name"=>"ProductNameBox", "msds_supplier_name"=>"SupplierNameBox",
            "msds_revision_date"=>"RevisionDateBox", "msds_revision_number"=>"RevisionNumberBox",
            "msds_supplier_phone"=>"PhoneBox", "msds_supplier_address"=>"AddressBox",
            "msds_un_number"=>"UnNumberBox", "msds_hazard_codes"=>"HazardCodesBox",
            "msds_autoignition"=>"AutoIgnitionBox", "msds_vapor_pressure"=>"VaporPressureBox",
            "msds_lel"=>"LelBox", "msds_uel"=>"UelBox", "msds_flash_point"=>"FlashPointBox",
            "msds_ppe"=>"PersonalProtectionBox", "msds_corrosive_flag"=>"CorrosivityBox",
            "msds_moel_submission_no"=>"MoelSubmissionBox", "msds_graco_code"=>"GracoCodeBox",
            "msds_molecular_formula"=>"MolecularFormulaBox", "msds_physical_state"=>"PhysicalStateBox", "msds_hazardous_goods_class"=>"HazardousGoodsBox", "msds_reactivity"=>"ReactivityBox", _=>""
        };
        private void ShowEvidence(int page,string source)
        {
            _highlightSource=source??"";
            _fallbackText=$"페이지: {page}\n선택 원문: {_highlightSource}";
            if(page>0) { _page=page; NavigatePdf(); }
            else { _highlightSource=""; _pdfStatus.Text="원문 페이지 미확인"; if(_fallbackButton!=null) _fallbackButton.Visibility=Visibility.Visible; NavigatePdf(); }
        }
        private void Apply(object sender,RoutedEventArgs e)
        {
            var user = (Owner as MainWindow)?.GetCurrentUser() ?? StartupUserService.FindCurrentUser();
            if (!QcsAuthorizationService.CanAccessProgram(user, QcsAuthorizationService.AiCorrectionProgramId))
            {
                WpfMessageBox.Show("AI 교정 및 학습데이터 저장 권한이 없습니다.", "권한 안내", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var changed=_rows.Where(r=>r.Value!=r.Original || r.Approved).ToList();
            if (changed.Count==0) { DialogResult=true;return; }
            if (WpfMessageBox.Show($"변경된 {changed.Count}개 항목을 등록 화면에 적용하시겠습니까?",
                "AI 교정 확인",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            try
            {
                CollectCorrections(changed);
                foreach(var row in changed)CorrectedValues[row.Key]=row.Value;
                DialogResult=true;
            }
            catch(Exception ex)
            {
                WpfMessageBox.Show("교정 정보 준비에 실패하여 적용하지 않았습니다.\n"+ex.Message,
                    "AI 교정 오류",MessageBoxButton.OK,MessageBoxImage.Error);
            }
        }
        private void CollectCorrections(List<FieldRow> changed)
        {
            using var stream = System.IO.File.OpenRead(_filePath);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            foreach (var row in changed.Where(r => r.Approved))
            {
                if (MsdsIngredientLearningKey.Parse(row.Key, out var validationCas, out var validationField) &&
                    (validationField == "Twa" || validationField == "Stel") &&
                    !MsdsSimilarDocumentLearning.ExposureEquivalent(row.Original, row.Value))
                    throw new InvalidOperationException($"'{row.Name}'의 교정값에서 농도 또는 노출시간이 변경·누락되었습니다. 원문 수치와 시간을 확인해 주세요.");
                var originalField = _analysis.Fields.FirstOrDefault(f=>MapField(f.FieldKey)==row.Key);
                MsdsIngredient? ingredient=null;
                if (MsdsIngredientLearningKey.Parse(row.Key,out var cas,out _))
                    ingredient=_analysis.Ingredients.FirstOrDefault(x=>x.CasNumber?.Trim()==cas);
                var source=row.Evidence?.Source ?? originalField?.SourceText ?? ingredient?.SourceText;
                var page=row.Evidence?.Page ?? originalField?.SourcePage ?? ingredient?.SourcePage;
                if (string.IsNullOrWhiteSpace(source) || page is null or <=0)
                    throw new InvalidOperationException($"'{row.Name}'의 PDF 원문 근거가 없어 학습을 승인할 수 없습니다.");
                PendingLearning.Add(new MsdsLearningEntry { Key=row.Key, Before=row.Original, After=row.Value,
                    Hash=hash, Label=row.Evidence?.Label, Text=source, Context=row.Evidence?.Context ?? source,
                    Page=page, Candidate=row.Evidence?.Value,
                    Method=row.Evidence==null?"MANUAL":row.Value==row.Evidence.Value?"SELECTED":"EDITED",
                    User=Environment.UserName, At=DateTimeOffset.Now.ToString("O") });
            }
        }
    }
}
