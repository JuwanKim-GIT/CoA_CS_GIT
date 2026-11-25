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
        public string Display => $"{Sequence:D3}  |  {Field}  |  {Kind}  |  {Scope}  |  {Status}  |  {Description}";
    }
    private readonly DataGrid _items = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = System.Windows.Controls.DataGridSelectionMode.Single, CanUserAddRows = false, SelectionUnit = System.Windows.Controls.DataGridSelectionUnit.FullRow };
    private readonly TextBox _search = new() { Height = 32, MinWidth = 180 };
    private readonly StackPanel _summary = new() { Orientation = Orientation.Horizontal };
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
    private readonly TextBox _field = new() { Height = 32 };
    private readonly TextBox _instruction = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _sequence = new() { Text = "100", Height = 32 };
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
    private Button? _embedButton;
    private bool _loading;
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
        var layout = new DockPanel { Margin = new Thickness(16) };
        var filters = new WrapPanel { Margin = new Thickness(0,0,0,12) };
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
        var buttons = new WrapPanel { Margin = new Thickness(0,0,0,8) };
        AddViewButton(buttons,"신규",(_,_)=>BeginNew());
        AddViewButton(buttons,"복제",(_,_)=>Duplicate());
        AddViewButton(buttons,"수정",(_,_)=>BeginEdit());
        AddViewButton(buttons,"삭제",(_,_)=>Delete());
        AddEditButton(buttons,"저장",(_,_)=>Save());
        AddEditButton(buttons,"취소",(_,_)=>CancelEdit());
        AddViewButton(buttons,"승인",(_,_)=>SetState("APPROVED"));
        AddViewButton(buttons,"비활성화",(_,_)=>SetState("DISABLED"));
        _embedButton=AddViewButton(buttons,"임베딩 생성",async (_,_)=>await EmbedAsync());
        DockPanel.SetDock(buttons,Dock.Top); layout.Children.Add(buttons);
        DockPanel.SetDock(_status,Dock.Bottom); layout.Children.Add(_status);
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3,GridUnitType.Star), MinHeight=130 });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2,GridUnitType.Star), MinHeight=140 });
        layout.Children.Add(body);
        var list = new DockPanel();
        DockPanel.SetDock(_count,Dock.Top); list.Children.Add(_count);
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

        foreach (var (headerText,path,width) in new (string,string,double)[] {
            ("순서","Sequence",55),("지침명","Name",145),("교정 항목","Field",85),("지침 유형","Kind",120),
            ("자연어 지침","Description",300),("적용 범위","Scope",85),("적용 PDF","PdfCount",80),("교정 건수","AppliedCount",80),("실패 건수","FailedCount",80),
            ("상태","Status",95),("최종 적용일시","LastApplied",155),("실행 조건","Condition",95),("품목군","Material",70),("GracoCode","Graco",100),("버전","Version",60),("임베딩","Embedding",90),("ID","Id",250) })
            _items.Columns.Add(new DataGridTextColumn { Header=headerText, Binding=new Binding(path), Width=new DataGridLength(width), ElementStyle=System.Windows.Application.Current.TryFindResource("QcsGridText") as Style });
        _items.MouseDoubleClick += (_,e) => {
            if (!Allowed() || _mode != EditMode.View || _items.SelectedItem is not Instruction rule) return;
            if (System.Windows.Controls.ItemsControl.ContainerFromElement(_items, e.OriginalSource as DependencyObject) is not System.Windows.Controls.DataGridRow) return;
            var window = new MsdsApplicationCasesWindow(rule.Id) { Owner = Window.GetWindow(this) };
            window.ShowDialog();
            Load();
        };
        _items.SelectionChanged += (_,_)=>Select(); list.Children.Add(_items); body.Children.Add(list);
        var splitter = new System.Windows.Controls.GridSplitter { Height=5, HorizontalAlignment=System.Windows.HorizontalAlignment.Stretch, Background=System.Windows.Media.Brushes.LightGray, ResizeDirection=System.Windows.Controls.GridResizeDirection.Rows };
        Grid.SetRow(splitter,1); body.Children.Add(splitter);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        Grid.SetRow(scroll,2); body.Children.Add(scroll);
        var editor = new StackPanel { Margin=new Thickness(0,8,0,4) }; scroll.Content=editor;
        editor.Children.Add(new TextBlock { Text="지침 상세 정보", FontSize=15, FontWeight=FontWeights.SemiBold });
        var fields = new System.Windows.Controls.Primitives.UniformGrid { Columns=4 };
        void FieldBox(string label, UIElement control) { var panel=new StackPanel { Margin=new Thickness(0,0,12,0) }; Add(panel,label,control); fields.Children.Add(panel); }
        FieldBox("지침명",_name); FieldBox("대상 필드 코드",_field); FieldBox("지침 유형",_kind); FieldBox("실행 순서",_sequence);
        FieldBox("적용 범위",_scope); FieldBox("품목군 (GROUP)",_material); FieldBox("GracoCode (PRODUCT)",_graco); FieldBox("실행 조건",_condition);
        editor.Children.Add(fields); _scope.SelectionChanged+=(_,_)=>ScopeControls();
        _instruction.MinHeight=75; Add(editor,"자연어 지침",_instruction);
        Content=layout;
        Loaded += (_, _) => { SetMode(EditMode.View); Load(); };
    }
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
    private void SetMode(EditMode mode)
    {
        _mode = mode;
        bool editing = mode != EditMode.View;
        foreach (var control in new TextBox[] { _field, _instruction, _sequence, _supplier, _product, _name, _graco })
        {
            control.IsReadOnly = !editing;
            control.Background = editing ? System.Windows.Media.Brushes.White :
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 234, 245));
        }
        _kind.IsEnabled = _scope.IsEnabled = editing;
        _condition.IsEnabled = editing;
        ScopeControls();
        _items.IsEnabled = !editing;
        _search.IsEnabled = !editing;
        foreach(var f in new[]{_fieldFilter,_kindFilter,_stateFilter,_scopeFilter}) f.IsEnabled=!editing;
        foreach (var button in _viewButtons) button.IsEnabled = !editing;
        foreach (var button in _editButtons) button.IsEnabled = editing;
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
        _selectedId ??= (_items.SelectedItem as Instruction)?.Id;
        if (_selectedId == null) { Notify("삭제할 지침을 선택하세요."); return; }

        if (MessageBox.Show("선택한 지침과 연결된 임베딩 벡터를 삭제하시겠습니까?", "지침 삭제",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) { Notify("삭제 취소"); return; }
        try
        {
        using (var historyDb = Open()) { using var check = historyDb.CreateCommand(); check.CommandText = "SELECT COUNT(*) FROM ai_applyd_det WHERE ai_applyd_learning_id=$id"; check.Parameters.AddWithValue("$id",_selectedId); if (Convert.ToInt64(check.ExecuteScalar()) > 0) { Notify("적용 이력이 있는 지침은 삭제할 수 없습니다. 비활성화를 사용하세요."); return; } }
            using var db = Open(); using var tx = db.BeginTransaction();
            using (var vector = db.CreateCommand())
            {
                vector.Transaction = tx;
                vector.CommandText = "DELETE FROM ai_instruction_vectord_det WHERE ai_instruction_vector_learning_id=$id";
                vector.Parameters.AddWithValue("$id", _selectedId); vector.ExecuteNonQuery();
            }
            // Legacy learning candidates can have case links with ON DELETE RESTRICT.
            // Remove only links owned by this instruction, never the correction history itself.
            using (var links = db.CreateCommand())
            {
                links.Transaction = tx;
                links.CommandText = "DELETE FROM ai_learningd_det WHERE ai_learningd_learning_id=$id";
                links.Parameters.AddWithValue("$id", _selectedId);
                links.ExecuteNonQuery();
            }
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM ai_learning_mstr WHERE ai_learning_id=$id";
            cmd.Parameters.AddWithValue("$id", _selectedId);
            int count = cmd.ExecuteNonQuery(); tx.Commit();
            _selectedId = null; Clear(); Load(); Notify(count == 1 ? "지침 삭제 완료" : "삭제 대상이 없습니다.");
        }
        catch (Exception ex) { Notify("삭제 실패: " + ex.Message); }
    }
    private async Task EmbedAsync()
    {
        if (!Allowed() || _mode != EditMode.View || _selectedId == null) { Notify("임베딩할 지침을 선택하세요."); return; }
        string id = _selectedId;
        try
        {
            if (_embedButton != null) _embedButton.IsEnabled = false;
            Notify("임베딩 생성 중...");
            var result = await MsdsNaturalInstructionEmbedding.GenerateAsync(id);
            Notify(result);
        }
        catch (Exception ex) { Notify("임베딩 실패: " + ex.Message); }
        finally { if (_embedButton != null) _embedButton.IsEnabled = true; Load(); }
    }
    private void Clear()
    {
        _items.SelectedItem = null; _selectedId = null; _field.Clear(); _instruction.Clear();
        _name.Clear(); _graco.Clear(); _condition.SelectedIndex = _material.SelectedIndex = 0;
        _sequence.Text = "100"; _supplier.Clear(); _product.Clear(); _kind.SelectedIndex = _scope.SelectedIndex = 0;
    }
    private void Select()
    {
        if (_loading || _mode != EditMode.View || _items.SelectedItem is not Instruction x) return;
        _selectedId = x.Id; _field.Text = x.Field; _instruction.Text = x.Description;
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
            q.CommandText = @"SELECT ai_learning_id,ai_learning_field_code,ai_learning_rule_type,ai_learning_scope,
COALESCE(ai_learning_supplier_name,''),COALESCE(ai_learning_product_name,''),ai_learning_status,
ai_learning_sequence,COALESCE(ai_learning_description,''),COALESCE(ai_learning_name,''),ai_learning_condition,
COALESCE(ai_learning_material_type,''),COALESCE(ai_learning_graco_code,''),ai_learning_version,
CASE WHEN ai_learning_embedding_id IS NOT NULL AND ai_learning_text_hash IS NOT NULL AND EXISTS(SELECT 1 FROM ai_instruction_vectord_det v WHERE v.ai_instruction_vector_learning_id=ai_learning_id AND v.ai_instruction_vector_embedding_id=ai_learning_embedding_id AND v.ai_instruction_vector_text_hash=ai_learning_text_hash) THEN '생성 완료' ELSE '미생성' END,
(SELECT COUNT(DISTINCT ai_applyd_apply_id) FROM ai_applyd_det WHERE ai_applyd_learning_id=ai_learning_id),
(SELECT COUNT(*) FROM ai_applyd_det WHERE ai_applyd_learning_id=ai_learning_id AND ai_applyd_status='APPLIED'),
(SELECT COUNT(*) FROM ai_applyd_det WHERE ai_applyd_learning_id=ai_learning_id AND ai_applyd_status='FAILED'),
COALESCE((SELECT MAX(ai_applyd_applied_at) FROM ai_applyd_det WHERE ai_applyd_learning_id=ai_learning_id),'') FROM ai_learning_mstr
WHERE substr(ai_learning_id,1,2)='NI'
ORDER BY ai_learning_sequence,ai_learning_created_at DESC";
            using var r = q.ExecuteReader();
            while (r.Read()) rows.Add(new(Read(r,0),Read(r,1),Read(r,2),Read(r,3),Read(r,4),Read(r,5),Read(r,6),r.GetInt32(7),Read(r,8),Read(r,9),Read(r,10),Read(r,11),Read(r,12),r.GetInt32(13),Read(r,14),r.GetInt32(15),r.GetInt32(16),r.GetInt32(17),Read(r,18)));
            _rows = rows;
            SetFilter(_fieldFilter, rows.Select(x=>x.Field));
            SetFilter(_kindFilter, rows.Select(x=>x.Kind));
            SetFilter(_stateFilter, rows.Select(x=>x.Status));
            SetFilter(_scopeFilter, rows.Select(x=>x.Scope));
            UpdateSummary(); Filter();
        }
        catch (Exception ex) { Notify("조회 실패: " + ex.Message); }
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
            q.CommandText="SELECT COUNT(DISTINCT d.ai_applyd_apply_id) FROM ai_applyd_det d JOIN ai_learning_mstr l ON l.ai_learning_id=d.ai_applyd_learning_id WHERE substr(l.ai_learning_id,1,2)='NI'";
            pdfs=Convert.ToInt64(q.ExecuteScalar());
        }
        foreach(var (label,value) in new (string,long)[]{("전체 지침",_rows.Count),("승인",_rows.Count(x=>x.Status=="APPROVED")),("대기",_rows.Count(x=>x.Status=="PENDING")),("비활성",_rows.Count(x=>x.Status=="DISABLED")),("적용 PDF",pdfs),("교정 성공",_rows.Sum(x=>x.AppliedCount)),("교정 실패",_rows.Sum(x=>x.FailedCount))}) {
            var panel=new StackPanel(); panel.Children.Add(new TextBlock { Text=label, FontWeight=FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text=value.ToString("N0")+" 건", FontSize=22, FontWeight=FontWeights.SemiBold, Foreground=System.Windows.Media.Brushes.DarkBlue });
            _summary.Children.Add(new System.Windows.Controls.Border { Child=panel, MinWidth=92, Padding=new Thickness(8,6,8,6), Margin=new Thickness(0,0,8,0), CornerRadius=new CornerRadius(6), Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226,234,245)) });
        }
    }
    private void Filter()
    {
        if (!Allowed() || _mode != EditMode.View) return;
        var words = _search.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool Match(ComboBox f,string value) => f.SelectedIndex<=0 || Equals(f.SelectedItem,value);
        var rows = _rows.Where(x => { string text=string.Join(" ",x.Id,x.Name,x.Field,x.Kind,x.Scope,x.Material,x.Graco,x.Supplier,x.Product,x.Condition,x.Status,x.Description,x.Embedding,x.Version,x.Sequence,x.PdfCount,x.AppliedCount,x.FailedCount,x.LastApplied); return Match(_fieldFilter,x.Field) && Match(_kindFilter,x.Kind) && Match(_stateFilter,x.Status) && Match(_scopeFilter,x.Scope) && words.All(w=>text.Contains(w,StringComparison.OrdinalIgnoreCase)); }).ToList();
        _loading=true; _items.ItemsSource=rows; _items.SelectedItem=rows.FirstOrDefault(x=>x.Id==_selectedId); _loading=false;
        if (_items.SelectedItem is Instruction) Select(); else Clear();
        _count.Text=$"총 {rows.Count}건 / 전체 {_rows.Count}건";
        Notify($"자연어 지침 {rows.Count} / {_rows.Count}건 조회 완료");
    }
    private void SaveNotice(string message)
    {
        Notify(message);
        MessageBox.Show(message, "지침 저장 안내", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void Save()
    {
        if (!Allowed() || _mode == EditMode.View) { SaveNotice("신규 또는 수정 상태에서 저장하세요."); return; }
        if (string.IsNullOrWhiteSpace(_field.Text) || string.IsNullOrWhiteSpace(_instruction.Text) ||
            !int.TryParse(_sequence.Text, out int sequence) || sequence < 0) { SaveNotice("필드, 지침, 실행 순서를 확인하세요."); return; }
        string scope = _scope.SelectedItem?.ToString() ?? "";
        if (scope != "GLOBAL" && scope != "GROUP" && scope != "PRODUCT") { SaveNotice("적용 범위를 선택하세요."); return; }
        if (scope == "GROUP" && _material.SelectedItem == null || scope == "PRODUCT" && _graco.Text.Trim().Length != 6) { SaveNotice("품목군 또는 6자리 GracoCode를 확인하세요."); return; }
        if (_mode == EditMode.New && string.IsNullOrWhiteSpace(_name.Text)) { SaveNotice("지침명을 입력하세요."); _name.Focus(); return; }
        try
        {
            using var db = Open(); using var tx = db.BeginTransaction(); using var q = db.CreateCommand(); q.Transaction = tx;
            var id = _selectedId ?? "NI" + Guid.NewGuid().ToString("N");
            q.CommandText = @"INSERT INTO ai_learning_mstr
(ai_learning_id,ai_learning_field_code,ai_learning_rule_type,ai_learning_description,ai_learning_scope,
ai_learning_supplier_name,ai_learning_product_name,ai_learning_status,ai_learning_version,
ai_learning_created_at,ai_learning_sequence,ai_learning_updated_at,ai_learning_name,ai_learning_condition,ai_learning_material_type,ai_learning_graco_code)
VALUES ($id,$field,$kind,$description,$scope,$supplier,$product,'PENDING',1,$now,$seq,$now,$name,$condition,$material,$graco)
ON CONFLICT(ai_learning_id) DO UPDATE SET ai_learning_field_code=excluded.ai_learning_field_code,
ai_learning_rule_type=excluded.ai_learning_rule_type,ai_learning_description=excluded.ai_learning_description,
ai_learning_scope=excluded.ai_learning_scope,ai_learning_supplier_name=excluded.ai_learning_supplier_name,
ai_learning_product_name=excluded.ai_learning_product_name,ai_learning_sequence=excluded.ai_learning_sequence,
ai_learning_name=excluded.ai_learning_name,ai_learning_condition=excluded.ai_learning_condition,ai_learning_material_type=excluded.ai_learning_material_type,ai_learning_graco_code=excluded.ai_learning_graco_code,
ai_learning_status='PENDING',ai_learning_version=ai_learning_mstr.ai_learning_version+1,
ai_learning_embedding_id=NULL,ai_learning_text_hash=NULL,ai_learning_updated_at=excluded.ai_learning_updated_at";
            q.Parameters.AddWithValue("$id", id); q.Parameters.AddWithValue("$field", _field.Text.Trim());
            q.Parameters.AddWithValue("$kind", _kind.SelectedItem?.ToString() ?? "EXTRACTION");
            q.Parameters.AddWithValue("$description", _instruction.Text.Trim()); q.Parameters.AddWithValue("$scope", scope);
            q.Parameters.AddWithValue("$supplier", DBNull.Value);
            q.Parameters.AddWithValue("$product", DBNull.Value);
            q.Parameters.AddWithValue("$name", _name.Text.Trim());
            q.Parameters.AddWithValue("$condition", _condition.SelectedItem?.ToString() ?? "ALWAYS");
            q.Parameters.AddWithValue("$material", scope == "GROUP" ? (object)_material.SelectedItem! : DBNull.Value);
            q.Parameters.AddWithValue("$graco", scope == "PRODUCT" ? (object)_graco.Text.Trim().ToUpperInvariant() : DBNull.Value);
            q.Parameters.AddWithValue("$seq", sequence); q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
            q.ExecuteNonQuery();
            // Old vectors remain for rollback, but pending instructions are never executed.
            tx.Commit(); _selectedId = id; SetMode(EditMode.View); Load(); Notify("지침 저장 완료 (승인 대기)");
        }
        catch (Exception ex) { SaveNotice("저장 실패: " + ex.Message); }
    }
    private void SetState(string state)
    {
        if (!Allowed() || _mode != EditMode.View || _selectedId == null) { Notify("Admin 권한 및 대상 선택이 필요합니다."); return; }
        if (MessageBox.Show($"선택한 지침을 {state} 상태로 변경하시겠습니까?", "지침 상태 변경", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        { Notify("상태 변경 취소"); return; }
        try
        {
            using var db = Open(); using var q = db.CreateCommand();
            q.CommandText = @"UPDATE ai_learning_mstr SET ai_learning_status=$state,
ai_learning_approved_by=CASE WHEN $state='APPROVED' THEN $by ELSE NULL END,
ai_learning_approved_at=CASE WHEN $state='APPROVED' THEN $now ELSE NULL END,
ai_learning_updated_at=$now WHERE ai_learning_id=$id";
            q.Parameters.AddWithValue("$state", state); q.Parameters.AddWithValue("$by", Owner?.GetCurrentUser()?.WindowsId ?? "");
            q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O")); q.Parameters.AddWithValue("$id", _selectedId);
            int count = q.ExecuteNonQuery(); Load(); Notify(count == 1 ? "상태 변경 완료" : "대상을 찾지 못했습니다.");
        }
        catch (Exception ex) { Notify("상태 변경 실패: " + ex.Message); }
    }
}
