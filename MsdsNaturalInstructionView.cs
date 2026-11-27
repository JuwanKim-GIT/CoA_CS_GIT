using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using Button = System.Windows.Controls.Button;
using DataGrid = System.Windows.Controls.DataGrid;
using Orientation = System.Windows.Controls.Orientation;
using Binding = System.Windows.Data.Binding;
using DataGridTextColumn = System.Windows.Controls.DataGridTextColumn;
using DataGridLength = System.Windows.Controls.DataGridLength;
using Control = System.Windows.Controls.Control;
using Style = System.Windows.Style;
using ScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using UserControl = System.Windows.Controls.UserControl;
using MessageBox = System.Windows.MessageBox;

namespace QCS;

// Phase 3: Admin-only instruction management and application statistics.
public sealed class MsdsNaturalInstructionView : UserControl
{
    public sealed record Instruction(string Id, string Field, string Kind, string Scope, string Supplier,
        string Product, string Status, int Sequence, string Description, string Name, string Condition, string Material, string Graco, int Version, string Embedding, int PdfCount, int AppliedCount, int FailedCount, string LastApplied)
    {
        public string Family { get; init; } = "";
        public bool Archived { get; init; }
        public Instruction? Active { get; init; }
        public int? ActiveVersion => Active?.Version;
        public int? PendingVersion => Status == "PENDING" ? Version : null;
        public string Display => $"{Sequence:D3}  |  {Field}  |  {Kind}  |  {Scope}  |  {Status}  |  {Description}";
    }
    private readonly DataGrid _items = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = System.Windows.Controls.DataGridSelectionMode.Single, CanUserAddRows = false, SelectionUnit = System.Windows.Controls.DataGridSelectionUnit.FullRow };
    private readonly TextBox _search = new() { Height = 32, MinWidth = 180 };
    private readonly WrapPanel _summary = new();
    private readonly TextBlock _detailStatus = new() { FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _detailCounts = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detailMetadata = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _count = new();
    private readonly ComboBox _fieldFilter = new() { MinWidth = 105, Height = 32 };
    private readonly ComboBox _kindFilter = new() { MinWidth = 125, Height = 32 };
    private readonly ComboBox _stateFilter = new() { MinWidth = 105, Height = 32 };
    private readonly ComboBox _scopeFilter = new() { MinWidth = 105, Height = 32 };
    private readonly TextBox _name = new() { Height = 32 };
    private readonly TextBox _graco = new() { Height = 32 };
    private readonly ComboBox _material = new() { ItemsSource = new[] { "RM", "FG" }, Height = 32, SelectedIndex = 0 };
    private readonly ComboBox _condition = new() { ItemsSource = new[] { "ALWAYS", "EMPTY", "NOT_EMPTY" }, Height = 32, SelectedIndex = 0 };
    private List<Instruction> _rows = new();
    private sealed record FieldOption(string Code, string Name) { public string Display => $"{Code}({Name})"; }
    private readonly ComboBox _field = new() { Height = 32, DisplayMemberPath = "Display", SelectedValuePath = "Code", IsEditable = false };
    private readonly List<FieldOption> _fieldOptions = new() {
        new("RevisionDateBox","개정일"),
        new("RevisionNumberBox","개정번호"),
        new("ProductNameBox","원료(제품)명"),
        new("SupplierNameBox","공급업체명"),
        new("PhoneBox","공급업체 전화"),
        new("AddressBox","공급업체 주소"),
        new("MoelSubmissionBox","노동부 제출번호"),
        new("HazardCodesBox","유해·위험 문구"),
        new("CorrosivityBox","피부 부식성/자극성"),
        new("PersonalProtectionBox","개인보호구"),
        new("ReactivityBox","반응성"),
        new("LelBox","폭발하한계 (LEL)"),
        new("UelBox","폭발상한계 (UEL)"),
        new("FlashPointBox","인화점"),
        new("AutoIgnitionBox","자연발화점"),
        new("VaporPressureBox","증기압"),
        new("UnNumberBox","UN 번호"),
        new("MolecularFormulaBox","분자식"),
        new("PhysicalStateBox","물리적 상태"),
        new("HazardousGoodsBox","위험물 분류"),
        new("ToxicityBox","독성치") };
    private void SelectField(string code)
    {
        if (!string.IsNullOrEmpty(code) && !_fieldOptions.Any(x=>x.Code==code)) { _fieldOptions.Add(new FieldOption(code,"기존 필드")); _field.Items.Refresh(); }
        _field.SelectedValue=code;
    }
    private readonly TextBox _instruction = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _sequence = new() { Text = "1", Height = 32, IsReadOnly = true };
    private readonly TextBox _supplier = new() { Height = 32 };
    private readonly TextBox _product = new() { Height = 32 };
    private readonly ComboBox _kind = new() { Height = 32, ItemsSource = new[] { "EXTRACTION", "FORMAT", "NORMALIZATION" }, SelectedIndex = 0 };
    private readonly ComboBox _scope = new() { Height = 32, ItemsSource = new[] { "GLOBAL", "GROUP", "PRODUCT" }, SelectedIndex = 0 };
    private readonly TextBlock _status = new() { Text = "자연어 지침 관리", Foreground = System.Windows.Media.Brushes.DarkSlateGray };
    private string? _selectedId;
    private enum EditMode { View, New, Edit }
    private EditMode _mode = EditMode.View;
    private readonly List<Button> _viewButtons = new();
    private readonly List<Button> _editButtons = new();
    private (Button Button, string Label)? _activeEditButton;
    private Button? _toggleStateButton;
    private bool _loading;
    private List<Instruction> _versions = new();
    private const string VersionSchema = @"CREATE TABLE IF NOT EXISTS ai_instruction_versionh_hist (
        ai_instruction_versionh_id TEXT PRIMARY KEY REFERENCES ai_learning_mstr(ai_learning_id) ON DELETE CASCADE,
        ai_instruction_versionh_family_id TEXT NOT NULL,
        ai_instruction_versionh_archived INTEGER NOT NULL DEFAULT 0
    )";
    private const string FamilyQuery = @"SELECT COALESCE(
        (SELECT ai_instruction_versionh_family_id FROM ai_instruction_versionh_hist
         WHERE ai_instruction_versionh_id=$id),$id)";
    private MainWindow? Owner => Window.GetWindow(this) as MainWindow;
    private bool Allowed() => Owner?.GetCurrentUser()?.UserLevel == QcsAuthorizationService.LevelAdmin;
    private static SqliteConnection Open() { var c = new SqliteConnection(DatabaseManager.GetConnectionString("msds")); c.Open(); return c; }
    private static string Read(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    private void Notify(string message) { _status.Text = message; Owner?.SetRunStatus(message); }
    private static void Add(StackPanel panel, string label, UIElement control)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 9, 0, 3), FontWeight = FontWeights.SemiBold });
        panel.Children.Add(control);
    }
    public MsdsNaturalInstructionView()
    {
        _fieldOptions.AddRange(MsdsUnifiedMemory.IngredientCodes.Zip(MsdsUnifiedMemory.IngredientNames,(code,name)=>new FieldOption(code,name)));
        _field.ItemsSource=_fieldOptions;
        var layout = new DockPanel { Margin = new Thickness(16) };
        var filters = new WrapPanel { Margin = new Thickness(0,0,0,12) };
        _items.Sorting += SortInstructions;
        void FilterBox(string label, System.Windows.Controls.Control control) {
            var panel = new StackPanel { Margin = new Thickness(0,0,10,4) };
            Add(panel,label,control); filters.Children.Add(panel);
        }
        FilterBox("교정 항목",_fieldFilter); FilterBox("지침 유형",_kindFilter);
        FilterBox("상태",_stateFilter); FilterBox("적용 범위",_scopeFilter);
        FilterBox("검색어 (공백 = AND)",_search);
        _search.KeyDown += (_,e) => { if (e.Key == System.Windows.Input.Key.Enter) Filter(); };
        var filterButtons = new WrapPanel { VerticalAlignment = VerticalAlignment.Bottom };
        filterButtons.Children.Add(MakeButton("조회", (_,_) => Filter()));
        filterButtons.Children.Add(MakeButton("초기화", (_,_) => { _search.Clear(); foreach(var f in new[]{_fieldFilter,_kindFilter,_stateFilter,_scopeFilter}) f.SelectedIndex=0; Filter(); }));
        filterButtons.Children.Add(MakeButton("새로고침", (_,_) => Load()));
        filters.Children.Add(filterButtons);
        var top = new WrapPanel { Margin = new Thickness(0,0,0,6) };
        filters.Margin = new Thickness(0,0,12,0);
        top.Children.Add(filters);
        _summary.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        top.Children.Add(_summary);
        DockPanel.SetDock(top,Dock.Top); layout.Children.Add(top);
        var listActions = new WrapPanel { Margin = new Thickness(0,0,0,4) };
        var buttons = new WrapPanel { Margin = new Thickness(0,8,0,0) };
        AddEditStartButton(buttons,"신규",BeginNew);
        AddEditStartButton(buttons,"복제",Duplicate);
        AddEditStartButton(buttons,"수정",BeginEdit);
        AddEditButton(buttons,"저장",async (_,_)=>await SaveAsync());
        AddViewButton(listActions,"승인",(_,_)=>SetState("APPROVED"));
        _toggleStateButton=AddViewButton(listActions,"비활성화",(_,_)=> { if (_items.SelectedItem is Instruction x) SetState(x.Status == "DISABLED" ? "APPROVED" : "DISABLED"); });
        AddViewButton(listActions,"삭제",(_,_)=>Delete());
        listActions.Children.Add(MakeButton("시험 실행",(_,_)=>OpenTrial()));
        AddViewButton(listActions,"적용사례",(_,_)=>OpenCases());
        
        DockPanel.SetDock(_status,Dock.Bottom); layout.Children.Add(_status);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 360 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 320 });
        layout.Children.Add(body);
        var list = new DockPanel { Margin = new Thickness(0,0,8,0) };
        var gridActions = new DockPanel();
        _count.Margin = new Thickness(12,0,2,4);
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.Foreground = System.Windows.Media.Brushes.DarkSlateGray;
        DockPanel.SetDock(_count, Dock.Right);
        gridActions.Children.Add(_count);
        gridActions.Children.Add(listActions);
        DockPanel.SetDock(gridActions, Dock.Top);
        list.Children.Add(gridActions);
        _items.Style = System.Windows.Application.Current.TryFindResource("QcsGrid") as Style;
        _items.RowStyle = System.Windows.Application.Current.TryFindResource("QcsGridRow") as Style;
        var rowStyle = new Style(typeof(System.Windows.Controls.DataGridRow), _items.RowStyle);
        var rowSelected = new System.Windows.Trigger { Property = System.Windows.Controls.DataGridRow.IsSelectedProperty, Value = true };
        rowSelected.Setters.Add(new System.Windows.Setter(Control.BackgroundProperty, System.Windows.Media.Brushes.DodgerBlue));
        rowSelected.Setters.Add(new System.Windows.Setter(Control.ForegroundProperty, System.Windows.Media.Brushes.White));
        rowStyle.Triggers.Add(rowSelected);
        _items.RowStyle = rowStyle;
        var cellStyle = new Style(typeof(System.Windows.Controls.DataGridCell), _items.CellStyle);
        var cellSelected = new System.Windows.Trigger { Property = System.Windows.Controls.DataGridCell.IsSelectedProperty, Value = true };
        cellSelected.Setters.Add(new System.Windows.Setter(Control.BackgroundProperty, System.Windows.Media.Brushes.DodgerBlue));
        cellSelected.Setters.Add(new System.Windows.Setter(Control.ForegroundProperty, System.Windows.Media.Brushes.White));
        cellStyle.Triggers.Add(cellSelected);
        _items.CellStyle = cellStyle;
        _items.Resources[System.Windows.SystemColors.InactiveSelectionHighlightBrushKey] = System.Windows.Media.Brushes.DodgerBlue;
        _items.Resources[System.Windows.SystemColors.InactiveSelectionHighlightTextBrushKey] = System.Windows.Media.Brushes.White;

        var gridText = System.Windows.Application.Current.TryFindResource("QcsGridText") as Style;
        var columnsMenu = new System.Windows.Controls.MenuItem { Header = "표시 열" };
        foreach (var (header, path, weight, visible) in new (string, string, double, bool)[] {
            ("순서","Sequence",0.55,true), ("지침명","Name",2,true), ("교정 항목","Field",1.6,true),
            ("지침 유형","Kind",1.4,true), ("적용 범위","Scope",0.9,true), ("상태","Status",1,true),
            ("자연어 지침","Description",3,false), ("적용 PDF","PdfCount",1,false),
            ("교정 건수","AppliedCount",1,false), ("실패 건수","FailedCount",1,false),
            ("최종 적용일시","LastApplied",1.8,false), ("실행 조건","Condition",1.2,false),
            ("품목군","Material",1,false), ("GracoCode","Graco",1.2,false),
            ("적용 버전",nameof(Instruction.ActiveVersion),0.8,true), ("승인 대기 버전",nameof(Instruction.PendingVersion),1.0,true), ("임베딩","Embedding",1.1,false), ("ID","Id",2.5,false) })
        {
            var textStyle = new Style(typeof(TextBlock), gridText);
            textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            textStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(path)));
            var column = new DataGridTextColumn
            {
                Header = header, Binding = new Binding(path), SortMemberPath = path, Width = new DataGridLength(weight, System.Windows.Controls.DataGridLengthUnitType.Star),
                ElementStyle = textStyle, Visibility = visible ? Visibility.Visible : Visibility.Collapsed
            };
            _items.Columns.Add(column);
            var option = new System.Windows.Controls.MenuItem { Header = header, IsCheckable = true, IsChecked = visible, StaysOpenOnClick = true };
            option.Checked += (_, _) => column.Visibility = Visibility.Visible;
            option.Unchecked += (_, _) => column.Visibility = Visibility.Collapsed;
            columnsMenu.Items.Add(option);
        }
        _items.ContextMenu = new System.Windows.Controls.ContextMenu();
        _items.ContextMenu.Items.Add(columnsMenu);
        _items.MouseDoubleClick += (_,e) => {
            if (!Allowed() || _mode != EditMode.View || _items.SelectedItem is not Instruction) return;
            if (System.Windows.Controls.ItemsControl.ContainerFromElement(_items, e.OriginalSource as DependencyObject) is not System.Windows.Controls.DataGridRow) return;
            var cell = FindGridCell(e.OriginalSource as DependencyObject);
            if (cell?.Column.SortMemberPath is nameof(Instruction.ActiveVersion) or nameof(Instruction.PendingVersion)) return;
            OpenCases();
        };
        _items.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var cell = FindGridCell(e.OriginalSource as DependencyObject);
            if (Allowed() && _mode == EditMode.View && cell?.DataContext is Instruction rule &&
                (cell.Column.SortMemberPath == nameof(Instruction.ActiveVersion) && rule.ActiveVersion.HasValue ||
                 cell.Column.SortMemberPath == nameof(Instruction.PendingVersion) && rule.PendingVersion.HasValue))
            {
                e.Handled = true;
                OpenVersions(rule);
            }
        };
        _items.SelectionChanged += (_,_)=>Select(); list.Children.Add(_items); body.Children.Add(list);
        var splitter = new System.Windows.Controls.GridSplitter
        {
            Width = 8, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Background = System.Windows.Media.Brushes.LightGray,
            ResizeDirection = System.Windows.Controls.GridResizeDirection.Columns
        };
        Grid.SetColumn(splitter,1); body.Children.Add(splitter);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll,2); body.Children.Add(scroll);
        var editor = new StackPanel { Margin = new Thickness(12,0,4,8) };
        scroll.Content = editor;
        editor.Children.Add(new TextBlock { Text = "지침 상세 정보", FontSize = 16, FontWeight = FontWeights.SemiBold });
        editor.Children.Add(_detailStatus);
        Add(editor,"지침명",_name);
        var fields = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        void FieldBox(string label, UIElement control)
        {
            var panel = new StackPanel { Margin = new Thickness(0,0,6,0) };
            Add(panel,label,control);
            fields.Children.Add(panel);
        }
        FieldBox("대상 필드 코드",_field); FieldBox("지침 유형",_kind);
        FieldBox("적용 범위",_scope); FieldBox("실행 조건",_condition);
        FieldBox("품목군 (GROUP)",_material); FieldBox("GracoCode (PRODUCT)",_graco);
        FieldBox("실행 순서",_sequence);
        editor.Children.Add(fields);
        _kind.SelectionChanged += (_, _) =>
        {
            if (_kind.SelectedItem is string kind)
                _sequence.Text = MsdsNaturalInstructionEngine.SequenceFor(kind).ToString();
        };
        _scope.SelectionChanged += (_,_) => ScopeControls();
        _instruction.Height = 160;
        Add(editor,"자연어 지침",_instruction);
        _detailCounts.Margin = new Thickness(0,12,0,4);
        editor.Children.Add(_detailCounts);
        editor.Children.Add(_detailMetadata);
        editor.Children.Add(buttons);
        Content=layout;
        Loaded += (_, _) => { SetMode(EditMode.View); Load(); };
    }
    private void OpenCases()
    {
        if (!Allowed() || _mode != EditMode.View || _items.SelectedItem is not Instruction rule) return;
        var window = new MsdsApplicationCasesWindow(rule.Family) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        Load();
    }

    private void OpenTrial()
    {
        if (!Allowed()) return;
        if (_field.SelectedValue is not string field || _kind.SelectedItem is not string kind ||
            _condition.SelectedItem is not string condition || string.IsNullOrWhiteSpace(_instruction.Text))
        {
            Notify("시험할 대상 필드, 지침 유형, 실행 조건, 지침 문구를 입력하세요.");
            return;
        }

        var picker = new Microsoft.Win32.OpenFileDialog { Title = "시험할 MSDS PDF 선택", Filter = "PDF 파일 (*.pdf)|*.pdf" };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        var path = picker.FileName;
        var selectedRule = _items.SelectedItem as Instruction;
        var scope = _scope.SelectedItem as string ?? selectedRule?.Scope ?? "GLOBAL";
        var draft = new MsdsNaturalInstructionEngine.Rule(_selectedId ?? "~시험", field, kind, _instruction.Text,
            scope, _supplier.Text, _product.Text, _material.SelectedItem as string ?? "", _graco.Text.Trim(),
            condition, MsdsNaturalInstructionEngine.SequenceFor(kind),
            _selectedId == null ? 0 : selectedRule?.Version ?? 0, selectedRule?.Active?.Id);
        bool editing = _mode != EditMode.View;
        var ruleLabels = _versions.ToDictionary(rule => rule.Id, rule => (rule.Name, rule.Condition));
        ruleLabels[draft.Id] = (_name.Text.Trim(), condition);
        var results = new StackPanel { Margin = new Thickness(20, 12, 20, 4) };
        void ShowMessage(string message)
        {
            results.Children.Clear();
            results.Children.Add(new TextBlock { Text = message, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap });
        }

        using var cancellation = new System.Threading.CancellationTokenSource();
        var mode = new ComboBox
        {
            ItemsSource = new[] { "선택 지침만 시험", "실제 적용 방식으로 시험" },
            SelectedIndex = 0, MinWidth = 185, Margin = new Thickness(8, 3, 0, 3)
        };
        var material = new ComboBox
        {
            ItemsSource = new[] { "RM", "FG" }, MinWidth = 70, Margin = new Thickness(8, 3, 0, 3)
        };
        var materialLabel = new TextBlock
        {
            Text = "시험 문서 품목군", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0)
        };
        var context = new TextBlock
        {
            Margin = new Thickness(8, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
            Foreground = TrialAccent
        };
        void UpdateTrialOptions()
        {
            context.Text = $"선택 지침: {_name.Text.Trim()} · {draft.ScopeLabel}" +
                $"   /   시험 문서 품목군: {material.SelectedItem ?? "선택 필요"}";
            ShowMessage("시험 시작 버튼을 누르세요.");
        }
        var start = MakeButton("▶  시험 시작", (_, _) => { });
        start.Width = 142;
        start.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        var status = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = TrialAccent };
        var statusBadge = new Border
        {
            Child = status, Background = TrialBlue, Padding = new Thickness(14, 7, 14, 7),
            CornerRadius = new CornerRadius(16), VerticalAlignment = VerticalAlignment.Center
        };
        var toolbar = new DockPanel { Background = TrialChrome, LastChildFill = true, Margin = new Thickness(16, 8, 16, 8) };
        DockPanel.SetDock(statusBadge, Dock.Right);
        toolbar.Children.Add(statusBadge);
        var options = new WrapPanel();
        options.Children.Add(start);
        options.Children.Add(mode);
        options.Children.Add(materialLabel);
        options.Children.Add(material);
        var toolbarContent = new StackPanel();
        toolbarContent.Children.Add(options);
        toolbarContent.Children.Add(context);
        toolbar.Children.Add(toolbarContent);

        var window = new Window
        {
            Owner = Window.GetWindow(this), Title = $"지침 시험 실행 · {System.IO.Path.GetFileName(path)}",
            Width = 1280, Height = 760, MinWidth = 640, MinHeight = 420,
            MaxWidth = SystemParameters.WorkArea.Width, MaxHeight = SystemParameters.WorkArea.Height,
            Background = System.Windows.Media.Brushes.White,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var close = new Button
        {
            Content = "닫기", Width = 100, Height = 34, IsCancel = true,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Background = System.Windows.Media.Brushes.White, Foreground = TrialAccent, BorderBrush = TrialAccent
        };
        close.Click += (_, _) => window.Close();
        var content = new DockPanel();
        var header = new Border { Child = toolbar, Background = TrialChrome, BorderBrush = TrialLine, BorderThickness = new Thickness(0, 0, 0, 1) };
        var footer = new Border { Child = close, Background = TrialChrome, Padding = new Thickness(16, 9, 16, 9), BorderBrush = TrialLine, BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(header);
        content.Children.Add(footer);
        var scroll = new ScrollViewer { Content = results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        content.Children.Add(scroll);
        window.Content = content;
        status.Text = "시험 대기";
        UpdateTrialOptions();
        mode.SelectionChanged += (_, _) => { status.Text = "시험 대기"; UpdateTrialOptions(); };
        material.SelectionChanged += (_, _) => { status.Text = "시험 대기"; UpdateTrialOptions(); };
        window.Closed += (_, _) => cancellation.Cancel();
        start.Click += async (_, _) =>
        {
            bool selectedOnly = mode.SelectedIndex == 0;
            if (material.SelectedItem is not string materialType)
            {
                ShowMessage("시험 문서 품목군(RM·FG)을 선택하세요.");
                return;
            }

            start.IsEnabled = mode.IsEnabled = material.IsEnabled = false;
            status.Text = "시험 실행 중";
            scroll.ScrollToTop();
            ShowMessage("PDF에서 값을 추출하고 지침을 적용하는 중입니다.");
            try
            {
                var memory = await MsdsRegistrationView.TrialNaturalInstructionAsync(path, materialType, draft, selectedOnly,
                    Allowed, cancellation.Token);
                if (cancellation.IsCancellationRequested) return;
                if (memory.TrialScopeMismatch is string mismatch)
                {
                    status.Text = "적용 범위 불일치";
                    ShowMessage(mismatch);
                    return;
                }

                status.Text = "✓  시험 완료";
                if (memory.NaturalHistory.Count == 0)
                {
                    ShowMessage("해당 항목에 적용할 지침이나 PDF 분석에서 확인된 대상 값이 없습니다.");
                    return;
                }

                results.Children.Clear();
                foreach (var group in memory.NaturalHistory.GroupBy(step => step.Target))
                    results.Children.Add(BuildTrialResult(group.Key, group.ToList(), ruleLabels, editing ? draft.Id : null));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (cancellation.IsCancellationRequested) return;
                status.Text = "시험 실패";
                ShowMessage($"시험 실행 실패 · {ex.Message}");
            }
            finally
            {
                if (!cancellation.IsCancellationRequested) start.IsEnabled = mode.IsEnabled = material.IsEnabled = true;
            }
        };
        window.ShowDialog();
    }

    private static readonly System.Windows.Media.Brush TrialAccent = TrialBrush(13, 57, 127);
    private static readonly System.Windows.Media.Brush TrialBlue = TrialBrush(227, 239, 253);
    private static readonly System.Windows.Media.Brush TrialChrome = TrialBrush(242, 245, 249);
    private static readonly System.Windows.Media.Brush TrialLine = TrialBrush(195, 207, 222);

    private static System.Windows.Media.Brush TrialBrush(byte red, byte green, byte blue)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static UIElement BuildTrialResult(MsdsUnifiedMemory.Row row, IReadOnlyList<MsdsUnifiedMemory.NaturalStep> steps,
        IReadOnlyDictionary<string, (string Name, string Condition)> ruleLabels, string? editingId)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        var title = string.Join(" · ", new[] { row.FieldName, row.CasNumber, row.IngredientName }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        section.Children.Add(new TextBlock
        {
            Text = title, FontSize = 23, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap
        });

        var comparison = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 10, 0, 12) };
        comparison.Children.Add(TrialValueCard("원 추출값", steps[0].Before, false));
        comparison.Children.Add(TrialValueCard("최종 결과값", row.FinalValue, true));
        section.Children.Add(comparison);
        section.Children.Add(new TextBlock
        {
            Text = "단계별 결과", FontSize = 19, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 7)
        });

        var table = new Grid();
        foreach (var width in new[] { new GridLength(66), new GridLength(1, GridUnitType.Star),
                     new GridLength(0.95, GridUnitType.Star), new GridLength(0.6, GridUnitType.Star),
                     new GridLength(1.2, GridUnitType.Star), new GridLength(3, GridUnitType.Star) })
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] headings = { "단계", "지침명", "적용 범위", "지침 버전", "처리 결과", "단계 결과값" };
        for (int column = 0; column < headings.Length; column++)
            AddTrialCell(table, 0, column, headings[column]);

        for (int index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var label = ruleLabels.GetValueOrDefault(step.RuleId);
            var name = string.IsNullOrWhiteSpace(label.Name) ? RuleKindLabel(step.Kind) : label.Name;
            var result = step.Status == "SKIPPED" && label.Condition is "EMPTY" or "NOT_EMPTY"
                ? $"조건 불충족 (실행 조건: {label.Condition})"
                : MsdsApplicationCasesWindow.FormatStatus(step.Status);
            var detail = step.Error;
            if (step.Page is int page)
                detail = string.Join(Environment.NewLine, new[] { detail, $"원문 근거 ({page}쪽) · {step.Evidence}" }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

            int gridRow = index + 1;
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddTrialCell(table, gridRow, 0, step.Sequence.ToString());
            AddTrialCell(table, gridRow, 1, name, step.Instruction);
            AddTrialCell(table, gridRow, 2, step.ScopeLabel);
            AddTrialCell(table, gridRow, 3, step.RuleId == editingId ? "편집본" : $"v{step.Version}");
            AddTrialCell(table, gridRow, 4, result, detail);
            AddTrialCell(table, gridRow, 5, DisplayValue(step.After));
        }

        section.Children.Add(new Border
        {
            Child = table, BorderBrush = TrialLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            ClipToBounds = true
        });
        return section;
    }

    private static void AddTrialCell(Grid table, int row, int column, string value, string? detail = null)
    {
        bool header = row == 0;
        var text = new TextBlock
        {
            Text = value, FontSize = 15, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = column == 0 ? TextAlignment.Center : TextAlignment.Left,
            FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = header ? TrialAccent : System.Windows.Media.Brushes.Black
        };
        var cell = new Border
        {
            Child = text, Padding = new Thickness(12, 8, 12, 8),
            Background = header ? TrialBlue : System.Windows.Media.Brushes.White,
            BorderBrush = TrialLine,
            BorderThickness = new Thickness(0, row == 0 ? 0 : 1, column == table.ColumnDefinitions.Count - 1 ? 0 : 1, 0)
        };
        if (!string.IsNullOrWhiteSpace(detail))
            cell.ToolTip = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        table.Children.Add(cell);
    }

    private static Border TrialValueCard(string label, string value, bool final)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = label, FontSize = 19, FontWeight = FontWeights.Bold, Foreground = TrialAccent,
            Margin = new Thickness(0, 0, 0, 5)
        });
        panel.Children.Add(new TextBox
        {
            Text = DisplayValue(value), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), FontSize = 16
        });
        return new Border
        {
            Child = panel, Padding = new Thickness(15, 10, 15, 12),
            Margin = final ? new Thickness(5, 0, 0, 0) : new Thickness(0, 0, 5, 0),
            Background = final ? TrialBlue : System.Windows.Media.Brushes.WhiteSmoke,
            CornerRadius = new CornerRadius(8)
        };
    }

    private static string DisplayValue(string value) => string.IsNullOrWhiteSpace(value) ? "(공란)" : value;

    private static string RuleKindLabel(string kind) => kind switch
    {
        "EXTRACTION" => "추출",
        "NORMALIZATION" => "표준화",
        "FORMAT" => "형식 정리",
        _ => kind
    };

    private Button MakeButton(string label, RoutedEventHandler click)
    {
        var b = new Button { Content = label, Width = 104, Height = 36, Margin = new Thickness(3), Style = TryFindResource("QcsPrimaryButton") as Style };
        b.Click += click; return b;
    }
    private Button AddViewButton(WrapPanel panel, string label, RoutedEventHandler click)
    {
        var button = MakeButton(label, click); _viewButtons.Add(button); panel.Children.Add(button); return button;
    }
    private Button AddEditButton(WrapPanel panel, string label, RoutedEventHandler click)
    {
        var button = MakeButton(label, click); _editButtons.Add(button); panel.Children.Add(button); return button;
    }
    private void AddEditStartButton(WrapPanel panel, string label, Action begin)
    {
        AddViewButton(panel, label, (sender, _) =>
        {
            var button = (Button)sender;
            if (_activeEditButton?.Button == button)
            {
                CancelEdit();
                return;
            }

            begin();
            if (_mode == EditMode.View) return;
            _activeEditButton = (button, label);
            button.Content = "취소";
            button.IsEnabled = true;
        });
    }
    private void SetMode(EditMode mode)
    {
        _mode = mode;
        if (mode == EditMode.View && _activeEditButton is { } active)
        {
            active.Button.Content = active.Label;
            _activeEditButton = null;
        }
        _detailStatus.Visibility = _detailCounts.Visibility = _detailMetadata.Visibility =
            mode == EditMode.New ? Visibility.Collapsed : Visibility.Visible;
        bool editing = mode != EditMode.View;
        foreach (var control in new TextBox[] { _instruction, _supplier, _product, _name, _graco })
        {
            control.IsReadOnly = !editing;
            control.Background = editing ? System.Windows.Media.Brushes.White :
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 234, 245));
        }
        _kind.IsEnabled = _scope.IsEnabled = editing;
        _condition.IsEnabled = editing; _field.IsEnabled=editing;
        ScopeControls();
        _items.IsEnabled = !editing;
        _search.IsEnabled = !editing;
        foreach(var f in new[]{_fieldFilter,_kindFilter,_stateFilter,_scopeFilter}) f.IsEnabled=!editing;
        foreach (var button in _viewButtons) button.IsEnabled = !editing;
        foreach (var button in _editButtons) button.IsEnabled = editing;
        UpdateStateButton();
    }
    private void BeginNew() { if (!Allowed()) return; Clear(); SetMode(EditMode.New); Notify("신규 지침 입력 중"); }
    private void BeginEdit()
    {
        if (!Allowed()) { Notify("Admin 권한이 필요합니다."); return; }
        _selectedId ??= (_items.SelectedItem as Instruction)?.Id;
        if (_selectedId == null) { Notify("수정할 지침을 선택하세요."); return; }
        SetMode(EditMode.Edit); Notify("지침 수정 중");
    }
    private void CancelEdit()
    {
        SetMode(EditMode.View); Load(); Notify("편집 취소");
    }
    private void Delete()
    {
        if (!Allowed() || _mode != EditMode.View) { Notify("Admin 권한이 필요합니다."); return; }
        if (_items.SelectedItem is not Instruction selected) { Notify("삭제할 지침을 선택하세요."); return; }

        if (MessageBox.Show($"'{selected.Name}' 지침의 모든 버전, 적용 이력, 임베딩을 영구 삭제하시겠습니까?",
            "지침 영구 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            Notify("삭제 취소");
            return;
        }

        try
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            string id = selected.Id;
            var exists = ExecuteVersionCommand(db, tx,
                "SELECT 1 FROM ai_learning_mstr WHERE ai_learning_id=$id", ("$id", id));
            if (exists == null)
            {
                Load();
                Notify("삭제 대상이 없습니다.");
                return;
            }

            string family = Convert.ToString(ExecuteVersionCommand(db, tx, FamilyQuery, ("$id", id))) ?? id;
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
CREATE TEMP TABLE instruction_delete_ids AS
SELECT ai_learning_id AS id FROM ai_learning_mstr WHERE ai_learning_id=$family
UNION
SELECT ai_instruction_versionh_id FROM ai_instruction_versionh_hist WHERE ai_instruction_versionh_family_id=$family;

CREATE TEMP TABLE instruction_delete_cases AS
SELECT DISTINCT ai_applyd_apply_id AS id FROM ai_applyd_det
WHERE ai_applyd_learning_id IN (SELECT id FROM instruction_delete_ids);

DELETE FROM ai_applyd_det WHERE ai_applyd_learning_id IN (SELECT id FROM instruction_delete_ids);
DELETE FROM ai_apply_mstr WHERE ai_apply_id IN (SELECT id FROM instruction_delete_cases)
    AND NOT EXISTS (SELECT 1 FROM ai_applyd_det WHERE ai_applyd_apply_id=ai_apply_id);
DELETE FROM ai_applyh_hist WHERE ai_applyh_learning_id IN (SELECT id FROM instruction_delete_ids);
DELETE FROM ai_instruction_vectord_det WHERE ai_instruction_vector_learning_id IN (SELECT id FROM instruction_delete_ids);
DELETE FROM ai_learningd_det WHERE ai_learningd_learning_id IN (SELECT id FROM instruction_delete_ids);
DELETE FROM ai_instruction_versionh_hist WHERE ai_instruction_versionh_id IN (SELECT id FROM instruction_delete_ids);
DELETE FROM ai_learning_mstr WHERE ai_learning_id IN (SELECT id FROM instruction_delete_ids);

DROP TABLE instruction_delete_cases;
DROP TABLE instruction_delete_ids;";
            cmd.Parameters.AddWithValue("$family", family);
            cmd.ExecuteNonQuery();
            tx.Commit();
            Clear();
            Load();
            Notify("지침과 관련 이력 삭제 완료");
        }
        catch (Exception ex) { Notify("삭제 실패: " + ex.Message); }
    }

    private void Clear()
    {
        _detailStatus.Text = _detailCounts.Text = _detailMetadata.Text = "";
        _items.SelectedItem = null; _selectedId = null; _field.SelectedIndex=-1; _instruction.Clear();
        _name.Clear(); _graco.Clear(); _condition.SelectedIndex = _material.SelectedIndex = 0;
        _sequence.Text = "1"; _supplier.Clear(); _product.Clear(); _kind.SelectedIndex = _scope.SelectedIndex = 0;
    }
    private void UpdateStateButton()
    {
        if (_toggleStateButton == null) return;
        var row = _items.SelectedItem as Instruction;
        _toggleStateButton.Content = row?.Status == "DISABLED" ? "활성화" : "비활성화";
        _toggleStateButton.IsEnabled = _mode == EditMode.View && row != null && (row.Active != null || row.Status == "DISABLED");
    }
    private void Select()
    {
        if (_loading) return;
        UpdateStateButton();
        if (_mode != EditMode.View || _items.SelectedItem is not Instruction x) return;
        _detailStatus.Text = x.Active?.Id != x.Id && x.Active != null ? $"적용 v{x.Active.Version} / 대기 v{x.Version}" : x.Status;
        _detailCounts.Text = $"적용 PDF {x.PdfCount:N0}건   ·   교정 성공 {x.AppliedCount:N0}건   ·   교정 실패 {x.FailedCount:N0}건";
        _detailMetadata.Text = $"임베딩 {x.Embedding}   ·   버전 {x.Version}\n최종 적용일시 {x.LastApplied}\nID {x.Id}";
        _selectedId = x.Id; SelectField(x.Field); _instruction.Text = x.Description;
        _sequence.Text = x.Sequence.ToString(); _supplier.Text = x.Supplier; _product.Text = x.Product;
        _name.Text=x.Name; _graco.Text=x.Graco; _material.SelectedItem=x.Material; if (_material.SelectedIndex < 0) _material.SelectedIndex=0; _condition.SelectedItem=x.Condition;
        _kind.SelectedItem = x.Kind; _scope.SelectedItem = x.Scope;
        if (x.Scope == "SUPPLIER" || (x.Scope == "PRODUCT" && string.IsNullOrWhiteSpace(x.Graco))) Notify("기존 공급업체 기반 지침입니다. 수정 시 새 적용 범위를 지정하세요.");
    }
    private void Load()
    {
        if (_mode != EditMode.View) return;
        if (!Allowed()) { _items.ItemsSource = null; Notify("Admin 전용: 접근이 제한되었습니다."); return; }
        try
        {
            var rows = new List<Instruction>();
            using var db = Open(); using var q = db.CreateCommand();
            q.CommandText = VersionSchema;
            q.ExecuteNonQuery();
            q.CommandText = @"SELECT ai_learning_id,ai_learning_field_code,ai_learning_rule_type,ai_learning_scope,
COALESCE(ai_learning_supplier_name,''),COALESCE(ai_learning_product_name,''),ai_learning_status,
ai_learning_sequence,COALESCE(ai_learning_description,''),COALESCE(ai_learning_name,''),ai_learning_condition,
COALESCE(ai_learning_material_type,''),COALESCE(ai_learning_graco_code,''),ai_learning_version,
CASE WHEN ai_learning_embedding_id IS NOT NULL AND ai_learning_text_hash IS NOT NULL AND EXISTS(SELECT 1 FROM ai_instruction_vectord_det v WHERE v.ai_instruction_vector_learning_id=ai_learning_id AND v.ai_instruction_vector_embedding_id=ai_learning_embedding_id AND v.ai_instruction_vector_text_hash=ai_learning_text_hash) THEN '생성 완료' ELSE '미생성' END,
(SELECT COUNT(DISTINCT ai_applyd_apply_id) FROM ai_applyd_det WHERE ai_applyd_learning_id IN (SELECT COALESCE(vh.ai_instruction_versionh_family_id,ai_learning_id) UNION SELECT ai_instruction_versionh_id FROM ai_instruction_versionh_hist WHERE ai_instruction_versionh_family_id=COALESCE(vh.ai_instruction_versionh_family_id,ai_learning_id)) AND COALESCE(ai_applyd_error,'') NOT LIKE '[SKIPPED] %'),
(SELECT COUNT(*) FROM ai_applyd_det WHERE ai_applyd_learning_id IN (SELECT COALESCE(vh.ai_instruction_versionh_family_id,ai_learning_id) UNION SELECT ai_instruction_versionh_id FROM ai_instruction_versionh_hist WHERE ai_instruction_versionh_family_id=COALESCE(vh.ai_instruction_versionh_family_id,ai_learning_id)) AND ai_applyd_status='APPLIED'),
(SELECT COUNT(*) FROM ai_applyd_det WHERE ai_applyd_learning_id IN (SELECT COALESCE(vh.ai_instruction_versionh_family_id,ai_learning_id) UNION SELECT ai_instruction_versionh_id FROM ai_instruction_versionh_hist WHERE ai_instruction_versionh_family_id=COALESCE(vh.ai_instruction_versionh_family_id,ai_learning_id)) AND ai_applyd_status='FAILED'),
COALESCE((SELECT MAX(ai_applyd_applied_at) FROM ai_applyd_det WHERE ai_applyd_learning_id IN (SELECT COALESCE(vh.ai_instruction_versionh_family_id,ai_learning_id) UNION SELECT ai_instruction_versionh_id FROM ai_instruction_versionh_hist WHERE ai_instruction_versionh_family_id=COALESCE(vh.ai_instruction_versionh_family_id,ai_learning_id)) AND COALESCE(ai_applyd_error,'') NOT LIKE '[SKIPPED] %'),''),COALESCE(vh.ai_instruction_versionh_family_id,ai_learning_id),COALESCE(vh.ai_instruction_versionh_archived,0)
FROM ai_learning_mstr LEFT JOIN ai_instruction_versionh_hist vh ON vh.ai_instruction_versionh_id=ai_learning_id
WHERE substr(ai_learning_id,1,2)='NI'
ORDER BY ai_learning_status,ai_learning_field_code,ai_learning_sequence,ai_learning_created_at DESC";
            using var r = q.ExecuteReader();
            while (r.Read()) rows.Add(new(Read(r,0),Read(r,1),Read(r,2),Read(r,3),Read(r,4),Read(r,5),Read(r,6),MsdsNaturalInstructionEngine.SequenceFor(Read(r,2)),Read(r,8),Read(r,9),Read(r,10),Read(r,11),Read(r,12),r.GetInt32(13),Read(r,14),r.GetInt32(15),r.GetInt32(16),r.GetInt32(17),Read(r,18)) { Family = Read(r,19), Archived = r.GetInt32(20) != 0 });
            _versions = rows;
            rows = rows.Where(version => !version.Archived).GroupBy(version => version.Family)
                .Select(group =>
                {
                    var current = group.OrderByDescending(version => version.Version).First();
                    return current with { Active = group.FirstOrDefault(version => version.Status == "APPROVED") };
                })
                .OrderBy(row => row.Status, StringComparer.Ordinal).ThenBy(row => row.Field, StringComparer.Ordinal)
                .ThenBy(row => row.Sequence).ToList();
            _rows = rows;
            SetFilter(_fieldFilter, rows.Select(x=>x.Field));
            SetFilter(_kindFilter, rows.Select(x=>x.Kind));
            SetFilter(_stateFilter, rows.Select(x=>x.Status));
            SetFilter(_scopeFilter, rows.Select(x=>x.Scope));
            UpdateSummary(); Filter();
        }
        catch (Exception ex) { Notify("조회 실패: " + ex.Message); }
    }
    private void SortInstructions(object sender, System.Windows.Controls.DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (_items.ItemsSource == null) return;
        var direction = e.Column.SortDirection == System.ComponentModel.ListSortDirection.Ascending
            ? System.ComponentModel.ListSortDirection.Descending
            : System.ComponentModel.ListSortDirection.Ascending;

        ApplyInstructionSort(e.Column.SortMemberPath, direction);
        foreach (var column in _items.Columns)
            column.SortDirection = column == e.Column ? direction : null;
    }

    private void ApplyInstructionSort(string selectedField, System.ComponentModel.ListSortDirection direction)
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_items.ItemsSource);
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new System.ComponentModel.SortDescription(selectedField, direction));
            foreach (var field in new[] { nameof(Instruction.Field), nameof(Instruction.Sequence) })
            {
                if (field != selectedField)
                    view.SortDescriptions.Add(new System.ComponentModel.SortDescription(field,
                        System.ComponentModel.ListSortDirection.Ascending));
            }
        }
    }
    private void ScopeControls()
    {
        _material.IsEnabled = _mode != EditMode.View && Equals(_scope.SelectedItem,"GROUP");
        _graco.IsReadOnly = _mode == EditMode.View || !Equals(_scope.SelectedItem,"PRODUCT");
    }
    private void Duplicate()
    {
        if (!Allowed() || _items.SelectedItem is not Instruction) { Notify("복제할 지침을 선택하세요."); return; }
        _selectedId=null; _name.Text += " (복제)"; SetMode(EditMode.New); Notify("복제 지침 입력 중");
    }
    private static void SetFilter(ComboBox box, IEnumerable<string> values)
    {
        var selected=box.SelectedItem as string;
        box.ItemsSource=new[]{"전체"}.Concat(values.Where(v=>!string.IsNullOrWhiteSpace(v)).Distinct().OrderBy(v=>v)).ToArray();
        box.SelectedItem=selected; if(box.SelectedIndex<0) box.SelectedIndex=0;
    }
    private void UpdateSummary()
    {
        _summary.Children.Clear();
        // PDF counts per rule can overlap; count distinct application IDs for the dashboard.
        long pdfs=0;
        using(var db=Open()) using(var q=db.CreateCommand()) {
            q.CommandText="SELECT COUNT(DISTINCT d.ai_applyd_apply_id) FROM ai_applyd_det d JOIN ai_learning_mstr l ON l.ai_learning_id=d.ai_applyd_learning_id WHERE substr(l.ai_learning_id,1,2)='NI' AND COALESCE(d.ai_applyd_error,'') NOT LIKE '[SKIPPED] %'";
            pdfs=Convert.ToInt64(q.ExecuteScalar());
        }
        foreach(var (label,value) in new (string,long)[]{("전체 지침",_rows.Count),("승인",_rows.Count(x=>x.Status=="APPROVED")),("대기",_rows.Count(x=>x.Status=="PENDING")),("비활성",_rows.Count(x=>x.Status=="DISABLED")),("적용 PDF",pdfs),("교정 성공",_rows.Sum(x=>x.AppliedCount)),("교정 실패",_rows.Sum(x=>x.FailedCount))}) {
            _summary.Children.Add(new TextBlock
            {
                Text = $"{label} {value:N0}건",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0,4,12,4)
            });
        }
    }
    private void Filter()
    {
        if (!Allowed() || _mode != EditMode.View) return;
        var words = _search.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool Match(ComboBox f,string value) => f.SelectedIndex<=0 || Equals(f.SelectedItem,value);
        var rows = _rows.Where(x => { string text=string.Join(" ",x.Id,x.Name,x.Field,x.Kind,x.Scope,x.Material,x.Graco,x.Supplier,x.Product,x.Condition,x.Status,x.Description,x.Embedding,x.Version,x.Sequence,x.PdfCount,x.AppliedCount,x.FailedCount,x.LastApplied); return Match(_fieldFilter,x.Field) && Match(_kindFilter,x.Kind) && Match(_stateFilter,x.Status) && Match(_scopeFilter,x.Scope) && words.All(w=>text.Contains(w,StringComparison.OrdinalIgnoreCase)); }).ToList();
        _loading = true;
        var sortColumn = _items.Columns.FirstOrDefault(column => column.SortDirection.HasValue);
        var sortDirection = sortColumn?.SortDirection;
        _items.ItemsSource = rows;
        if (sortColumn != null && sortDirection.HasValue)
        {
            ApplyInstructionSort(sortColumn.SortMemberPath, sortDirection.Value);
            sortColumn.SortDirection = sortDirection;
        }
        _items.SelectedItem = rows.FirstOrDefault(x => x.Id == _selectedId);
        _loading = false;
        if (_items.SelectedItem is Instruction) Select(); else Clear();
        UpdateStateButton();
        _count.Text=$"총 {rows.Count}건 / 전체 {_rows.Count}건";
        Notify($"자연어 지침 {rows.Count} / {_rows.Count}건 조회 완료");
    }
    private void SaveNotice(string message)
    {
        Notify(message);
        MessageBox.Show(message, "지침 저장 안내", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private async Task SaveAsync()
    {
        if (!Allowed() || _mode == EditMode.View) { SaveNotice("신규 또는 수정 상태에서 저장하세요."); return; }
        if (_field.SelectedValue is not string field || _kind.SelectedItem is not string kind ||
            string.IsNullOrWhiteSpace(_instruction.Text)) { SaveNotice("필드, 지침 유형, 지침을 확인하세요."); return; }
        int sequence = MsdsNaturalInstructionEngine.SequenceFor(kind);
        string scope = _scope.SelectedItem?.ToString() ?? "";
        if (scope != "GLOBAL" && scope != "GROUP" && scope != "PRODUCT") { SaveNotice("적용 범위를 선택하세요."); return; }
        if (scope == "GROUP" && _material.SelectedItem == null || scope == "PRODUCT" && _graco.Text.Trim().Length != 6) { SaveNotice("품목군 또는 6자리 GracoCode를 확인하세요."); return; }
        if (_mode == EditMode.New && string.IsNullOrWhiteSpace(_name.Text)) { SaveNotice("지침명을 입력하세요."); _name.Focus(); return; }
        var id = "NI" + Guid.NewGuid().ToString("N");
        string description = _instruction.Text.Trim();
        string name = _name.Text.Trim();
        string condition = _condition.SelectedItem as string ?? "ALWAYS";
        string material = scope == "GROUP" ? (string)_material.SelectedItem! : "";
        string graco = scope == "PRODUCT" ? _graco.Text.Trim().ToUpperInvariant() : "";
        try
        {
            using var db = Open(); using var tx = db.BeginTransaction(); using var q = db.CreateCommand(); q.Transaction = tx;
            string family = id;
            int version = 1;
            if (_selectedId is string previousId)
            {
                family = Convert.ToString(ExecuteVersionCommand(db, tx, FamilyQuery, ("$id", previousId))) ?? previousId;
                const string latestSql = @"SELECT ai_learning_id,ai_learning_version,ai_learning_status,
                    COALESCE(ai_instruction_versionh_archived,0),ai_learning_field_code,ai_learning_rule_type,
                    ai_learning_scope,ai_learning_condition,ai_learning_name,ai_learning_description,
                    ai_learning_material_type,ai_learning_graco_code FROM ai_learning_mstr
                    LEFT JOIN ai_instruction_versionh_hist ON ai_instruction_versionh_id=ai_learning_id
                    WHERE ai_learning_id=$family OR ai_instruction_versionh_family_id=$family
                    ORDER BY ai_learning_version DESC LIMIT 1";
                q.CommandText = latestSql;
                q.Parameters.AddWithValue("$family", family);
                string previousStatus;
                using (var reader = q.ExecuteReader())
                {
                    if (!reader.Read() || reader.GetString(0) != previousId || reader.GetInt32(3) != 0)
                        throw new InvalidOperationException("다른 작업에서 지침이 변경되었습니다. 새로고침 후 다시 수정하세요.");
                    bool unchanged = Read(reader, 4) == field && Read(reader, 5) == kind &&
                        Read(reader, 6) == scope && Read(reader, 7) == condition &&
                        Read(reader, 8).Trim() == name &&
                        Read(reader, 9).Trim().ReplaceLineEndings("\n") == description.ReplaceLineEndings("\n") &&
                        (scope != "GROUP" || Read(reader, 10) == material) &&
                        (scope != "PRODUCT" || string.Equals(Read(reader, 11).Trim(), graco, StringComparison.OrdinalIgnoreCase));
                    if (unchanged)
                    {
                        Notify("변경 사항이 없습니다.");
                        return;
                    }
                    version = reader.GetInt32(1) + 1;
                    previousStatus = reader.GetString(2);
                }
                q.Parameters.Clear();
                if (previousStatus != "APPROVED")
                    ArchiveVersion(db, tx, previousId, family);
            }

            q.CommandText = @"INSERT INTO ai_learning_mstr
(ai_learning_id,ai_learning_field_code,ai_learning_rule_type,ai_learning_description,ai_learning_scope,
ai_learning_supplier_name,ai_learning_product_name,ai_learning_status,ai_learning_version,
ai_learning_created_at,ai_learning_sequence,ai_learning_updated_at,ai_learning_name,ai_learning_condition,ai_learning_material_type,ai_learning_graco_code)
VALUES ($id,$field,$kind,$description,$scope,$supplier,$product,'PENDING',$version,$now,$seq,$now,$name,$condition,$material,$graco)";
            q.Parameters.AddWithValue("$version", version);
            q.Parameters.AddWithValue("$id", id); q.Parameters.AddWithValue("$field", field);
            q.Parameters.AddWithValue("$kind", kind);
            q.Parameters.AddWithValue("$description", description); q.Parameters.AddWithValue("$scope", scope);
            q.Parameters.AddWithValue("$supplier", DBNull.Value);
            q.Parameters.AddWithValue("$product", DBNull.Value);
            q.Parameters.AddWithValue("$name", name);
            q.Parameters.AddWithValue("$condition", condition);
            q.Parameters.AddWithValue("$material", scope == "GROUP" ? (object)material : DBNull.Value);
            q.Parameters.AddWithValue("$graco", scope == "PRODUCT" ? (object)graco : DBNull.Value);
            q.Parameters.AddWithValue("$seq", sequence); q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
            q.ExecuteNonQuery();
            ExecuteVersionCommand(db, tx, @"INSERT INTO ai_instruction_versionh_hist
                (ai_instruction_versionh_id,ai_instruction_versionh_family_id) VALUES ($id,$family)",
                ("$id", id), ("$family", family));
            tx.Commit();
        }
        catch (Exception ex)
        {
            SaveNotice("저장 실패: " + ex.Message);
            return;
        }

        _selectedId = id;
        SetMode(EditMode.View);
        Load();
        Notify("지침 저장 완료 / 임베딩 처리 중");

        string embeddingMessage;
        try
        {
            embeddingMessage = await MsdsNaturalInstructionEmbedding.GenerateAsync(id, description);
        }
        catch (Exception ex)
        {
            embeddingMessage = "임베딩 생성 실패: " + ex.Message;
        }
        if (_selectedId == id && _mode == EditMode.View)
            Load();
        Notify("지침 저장 완료 / " + embeddingMessage);
    }

    private static object? ExecuteVersionCommand(SqliteConnection db, SqliteTransaction tx, string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command.ExecuteScalar();
    }

    private static void ArchiveVersion(SqliteConnection db, SqliteTransaction tx, string id, string family)
    {
        ExecuteVersionCommand(db, tx, @"INSERT INTO ai_instruction_versionh_hist
            (ai_instruction_versionh_id,ai_instruction_versionh_family_id,ai_instruction_versionh_archived)
            VALUES ($id,$family,1) ON CONFLICT(ai_instruction_versionh_id)
            DO UPDATE SET ai_instruction_versionh_archived=1", ("$id", id), ("$family", family));
        ExecuteVersionCommand(db, tx, "UPDATE ai_learning_mstr SET ai_learning_status='DISABLED' WHERE ai_learning_id=$id",
            ("$id", id));
    }

    private static void PrepareApproval(SqliteConnection db, SqliteTransaction tx, string id, string family)
    {
        const string duplicateSql = @"SELECT other.ai_learning_id FROM ai_learning_mstr other
            JOIN ai_learning_mstr selected ON selected.ai_learning_id=$id
            LEFT JOIN ai_instruction_versionh_hist h ON h.ai_instruction_versionh_id=other.ai_learning_id
            WHERE substr(other.ai_learning_id,1,2)='NI' AND other.ai_learning_status='APPROVED'
            AND COALESCE(h.ai_instruction_versionh_family_id,other.ai_learning_id)<>$family
            AND trim(other.ai_learning_field_code)=trim(selected.ai_learning_field_code) COLLATE NOCASE
            AND other.ai_learning_rule_type=selected.ai_learning_rule_type
            AND other.ai_learning_scope=selected.ai_learning_scope
            AND CASE other.ai_learning_scope
                WHEN 'GROUP' THEN other.ai_learning_material_type=selected.ai_learning_material_type
                WHEN 'PRODUCT' THEN COALESCE(other.ai_learning_graco_code,'')=COALESCE(selected.ai_learning_graco_code,'') COLLATE NOCASE
                    AND (COALESCE(other.ai_learning_graco_code,'')<>'' OR
                        (other.ai_learning_supplier_name=selected.ai_learning_supplier_name COLLATE NOCASE AND
                         other.ai_learning_product_name=selected.ai_learning_product_name COLLATE NOCASE))
                WHEN 'SUPPLIER' THEN other.ai_learning_supplier_name=selected.ai_learning_supplier_name COLLATE NOCASE
                ELSE 1 END LIMIT 1";
        if (ExecuteVersionCommand(db, tx, duplicateSql, ("$id", id), ("$family", family)) is string)
            throw new InvalidOperationException("같은 교정 항목·유형·범위·대상에 승인된 지침이 있습니다.");

        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = @"SELECT ai_learning_id FROM ai_learning_mstr
            LEFT JOIN ai_instruction_versionh_hist ON ai_instruction_versionh_id=ai_learning_id
            WHERE (ai_learning_id=$family OR ai_instruction_versionh_family_id=$family)
              AND ai_learning_id<>$id AND COALESCE(ai_instruction_versionh_archived,0)=0";
        command.Parameters.AddWithValue("$family", family);
        command.Parameters.AddWithValue("$id", id);
        var previous = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) previous.Add(reader.GetString(0));
        foreach (string previousId in previous)
            ArchiveVersion(db, tx, previousId, family);
    }

    private static System.Windows.Controls.DataGridCell? FindGridCell(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is System.Windows.Controls.DataGridCell cell) return cell;
            source = source is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private void OpenVersions(Instruction rule)
    {
        var versions = _versions.Where(v => v.Family == rule.Family).OrderByDescending(v => v.Version).ToList();
        var columns = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(12) };
        for (int index = 0; index < 2; index++)
        {
            var panel = new DockPanel { Margin = new Thickness(5) };
            var picker = new ComboBox { Margin = new Thickness(0, 0, 0, 8),
                ItemsSource = versions.Select(v => $"v{v.Version} · {(v.Archived ? "이력" : v.Status switch
                { "APPROVED" => "적용", "PENDING" => "승인 대기", _ => "비활성" })}").ToArray() };
            var details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10) };
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedIndex < 0) return;
                var v = versions[picker.SelectedIndex];
                string scope = v.Scope switch
                {
                    "GROUP" => $"GROUP · {v.Material}",
                    "PRODUCT" => $"PRODUCT · {(string.IsNullOrWhiteSpace(v.Graco) ? v.Supplier + " / " + v.Product : v.Graco)}",
                    "SUPPLIER" => $"SUPPLIER · {v.Supplier}",
                    _ => v.Scope
                };
                details.Text = $"{v.Name}\n{v.Field} · {RuleKindLabel(v.Kind)}\n\n적용 범위  {scope}\n실행 조건  {v.Condition}\n\n{v.Description}";
            };
            picker.SelectedIndex = index == 0 && versions.Count > 1
                ? (rule.Active is Instruction active && active.Id != rule.Id
                    ? versions.FindIndex(v => v.Id == active.Id) : 1)
                : 0;
            DockPanel.SetDock(picker, Dock.Top);
            panel.Children.Add(picker);
            panel.Children.Add(details);
            columns.Children.Add(panel);
        }
        new Window { Owner = Window.GetWindow(this), Title = "지침 버전 비교", Width = 900, Height = 530,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = columns }.ShowDialog();
    }

    private void SetState(string state)
    {
        if (!Allowed() || _mode != EditMode.View || _items.SelectedItem is not Instruction row) { Notify("Admin 권한 및 대상 선택이 필요합니다."); return; }
        string id = state == "DISABLED" ? row.Active?.Id ?? row.Id : row.Id;
        if (MessageBox.Show($"선택한 지침을 {state} 상태로 변경하시겠습니까?", "지침 상태 변경", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        { Notify("상태 변경 취소"); return; }
        try
        {
            using var db = Open(); using var tx = db.BeginTransaction(); using var q = db.CreateCommand();
            q.Transaction = tx;
            var family = Convert.ToString(ExecuteVersionCommand(db, tx, FamilyQuery, ("$id", id))) ?? id;
            var archived = ExecuteVersionCommand(db, tx, @"SELECT COALESCE(ai_instruction_versionh_archived,0)
                FROM ai_learning_mstr LEFT JOIN ai_instruction_versionh_hist ON ai_instruction_versionh_id=ai_learning_id
                WHERE ai_learning_id=$id", ("$id", id));
            if (archived == null || Convert.ToInt32(archived) != 0)
                throw new InvalidOperationException("지침이 변경되었습니다. 새로고침 후 다시 시도하세요.");

            if (state == "APPROVED")
                PrepareApproval(db, tx, id, family);

            q.CommandText = @"UPDATE ai_learning_mstr SET ai_learning_status=$state,
ai_learning_approved_by=CASE WHEN $state='APPROVED' THEN $by ELSE NULL END,
ai_learning_approved_at=CASE WHEN $state='APPROVED' THEN $now ELSE NULL END,
ai_learning_updated_at=$now WHERE ai_learning_id=$id";
            q.Parameters.AddWithValue("$state", state); q.Parameters.AddWithValue("$by", Owner?.GetCurrentUser()?.WindowsId ?? "");
            q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O")); q.Parameters.AddWithValue("$id", id);
            int count = q.ExecuteNonQuery();
            tx.Commit();
            db.Close();
            Load(); Notify(count == 1 ? "상태 변경 완료" : "대상을 찾지 못했습니다.");
        }
        catch (Exception ex) { Notify("상태 변경 실패: " + ex.Message); }
    }
}
