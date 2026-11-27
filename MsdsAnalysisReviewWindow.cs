using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Binding = System.Windows.Data.Binding;
using SystemColors = System.Windows.SystemColors;
using TabControl = System.Windows.Controls.TabControl;
using TabItem = System.Windows.Controls.TabItem;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Orientation = System.Windows.Controls.Orientation;

namespace QCS;
internal sealed class MsdsAnalysisReviewWindow : Window
{
    private MsdsUnifiedMemory _memory = new();
    private readonly TextBlock _heading = new() { FontSize=18, FontWeight=FontWeights.SemiBold };
    private readonly WrapPanel _info = new();
    private readonly TextBlock _notice = new() { TextWrapping=TextWrapping.Wrap, Foreground=Paint(71,85,105) };
    private Button? _export;
    internal Action? BeforeExport { get; set; }
    private bool _hasData;
    private readonly string _placementPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QCS", "analysis-review-window.json");
    private sealed record Placement(double Left,double Top,double Width,double Height,bool Maximized);
    private void RestorePlacement() {
        try { var p=System.Text.Json.JsonSerializer.Deserialize<Placement>(System.IO.File.ReadAllText(_placementPath));
            if(p==null || !double.IsFinite(p.Left)||!double.IsFinite(p.Top)||!double.IsFinite(p.Width)||!double.IsFinite(p.Height)) return;
            var rect=new Rect(p.Left,p.Top,p.Width,p.Height);
            var desktop=new Rect(SystemParameters.VirtualScreenLeft,SystemParameters.VirtualScreenTop,SystemParameters.VirtualScreenWidth,SystemParameters.VirtualScreenHeight);
            rect.Intersect(desktop); if(rect.IsEmpty||rect.Width<100||rect.Height<100) return;
            WindowStartupLocation=WindowStartupLocation.Manual; Left=p.Left;Top=p.Top;Width=Math.Max(MinWidth,p.Width);Height=Math.Max(MinHeight,p.Height); if(p.Maximized) WindowState=WindowState.Maximized;
        } catch { }
    }
    private void SavePlacement() { try { var r=RestoreBounds; if(r.IsEmpty) return; System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_placementPath)!); System.IO.File.WriteAllText(_placementPath,System.Text.Json.JsonSerializer.Serialize(new Placement(r.Left,r.Top,r.Width,r.Height,WindowState==WindowState.Maximized))); } catch { } }
    internal void SetData(MsdsUnifiedMemory? memory,string title="",bool stored=false,string materialType="",string analysisTime="",string gracoCode="",string notice="") {
        _hasData=memory!=null; _memory=memory??new MsdsUnifiedMemory(); _heading.Text=_hasData?title:"분석결과 없음"; _info.Children.Clear();
        void Info(string label,string? value) { var panel=new StackPanel { Margin=new Thickness(0,0,18,6) }; panel.Children.Add(new TextBlock { Text=label,Foreground=Paint(71,85,105) }); panel.Children.Add(new TextBlock { Text=Show(value),FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,MaxWidth=340 }); _info.Children.Add(panel); }
        if(_hasData) { Info("GracoCode",gracoCode); Info("제품명",_memory.Basic("ProductNameBox")?.FinalValue); Info("구분",materialType=="RM"?"원료 (RM)":materialType=="FG"?"제품 (FG)":materialType); Info("분석일시",analysisTime); Info("공급업체",_memory.Basic("SupplierNameBox")?.FinalValue); }
        _notice.Text=!_hasData?(notice.Length>0?notice:"분석 완료된 MSDS를 선택하세요."):stored?"저장본: 과거 원본 미보존 또는 이력 누락 항목은 구분할 수 없습니다.":"";
        _notice.Visibility=string.IsNullOrEmpty(_notice.Text)?Visibility.Collapsed:Visibility.Visible;
        if(_export!=null) _export.IsEnabled=_hasData; Refresh();
    }
    private readonly DataGrid _values = MakeGrid(), _steps = MakeGrid();
    private readonly TextBox _instruction = new() { IsReadOnly=true, TextWrapping=TextWrapping.Wrap, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
    private readonly TabControl _category = new();
    private readonly ComboBox _filter = new() { Width=150 };
    private readonly TextBlock _total = new(), _corrected = new(), _modified = new();
    private readonly Dictionary<(string,int),string> _ruleNames = new();
    private static SolidColorBrush Paint(byte r,byte g,byte b)=>new(Color.FromRgb(r,g,b));
    private static string Show(string? value)=>string.IsNullOrWhiteSpace(value)?"—":value;
    private readonly TextBlock _status = new();
    private sealed record DisplayRow(MsdsUnifiedMemory.Row Source, string Item, string Ai, string Correction, string User, string Final)
    {
        public string ItemTooltip => Source.Category == "구성성분" ? Source.IngredientName : Item;
    }
    private string ItemHeader => _category.SelectedIndex == 1 ? "CAS No. / 항목명" : "항목명";
    private sealed record StepRow(int Sequence, string Rule, int Version, string Kind, string Before, string After, string Status, string Instruction);
    private static DataGrid MakeGrid() => new() { AutoGenerateColumns=false, IsReadOnly=true, CanUserAddRows=false, SelectionMode=DataGridSelectionMode.Single, SelectionUnit=DataGridSelectionUnit.FullRow };
    private void Setup(DataGrid grid, params (string Header,string Path,double Width)[] columns)
    {
        grid.Style=TryFindResource("QcsGrid") as Style; 
        grid.RowHeight=double.NaN; grid.MinRowHeight=38;
        var cellStyle=new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(DataGridCell.BackgroundProperty,Brushes.Transparent));
        cellStyle.Setters.Add(new Setter(DataGridCell.BorderThicknessProperty,new Thickness(0)));
        var selected=new Trigger { Property=DataGridCell.IsSelectedProperty,Value=true };
        selected.Setters.Add(new Setter(DataGridCell.BackgroundProperty,Paint(219,234,254)));
        selected.Setters.Add(new Setter(DataGridCell.ForegroundProperty,Paint(30,58,95)));
        cellStyle.Triggers.Add(selected); grid.CellStyle=cellStyle;
        grid.Resources[SystemColors.InactiveSelectionHighlightBrushKey]=Paint(219,234,254);
        grid.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey]=Paint(30,58,95);
        foreach(var c in columns) {
            var style=new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextWrappingProperty,TextWrapping.Wrap));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(TextBlock.LineStackingStrategyProperty,LineStackingStrategy.BlockLineHeight));
            style.Setters.Add(new Setter(TextBlock.LineHeightProperty,17d));
            style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty,46d));
            style.Setters.Add(new Setter(TextBlock.PaddingProperty,new Thickness(8,6,8,6)));
            if(c.Path=="Correction"||c.Path=="User") style.Setters.Add(new Setter(TextBlock.ForegroundProperty,c.Path=="Correction"?Paint(37,99,235):Paint(21,128,61)));
            style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty,VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new Binding(c.Path == "Item" ? "ItemTooltip" : c.Path)));
            grid.Columns.Add(new DataGridTextColumn { Header=c.Header, Binding=new Binding(c.Path), Width=new DataGridLength(c.Width,DataGridLengthUnitType.Star), ElementStyle=style });
        }
    }
    private Button Action(string name, Action action) { var b=new Button { Content=name, MinWidth=100, Height=34, Margin=new Thickness(4), Style=TryFindResource("QcsPrimaryButton") as Style }; b.Click+=(_,_)=>action(); return b; }
    internal MsdsAnalysisReviewWindow(MsdsUnifiedMemory memory,string title,List<string[]>? logs,Action? batchExport=null,bool stored=false,string materialType="",string analysisTime="",string gracoCode="")
    {
        LoadRuleNames(); Title="QCS - MSDS 분석결과 조회"; Width=1250; Height=820; MinWidth=850; MinHeight=550; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        RestorePlacement(); Closing+=(_,_)=>SavePlacement();
        var root=new DockPanel { Margin=new Thickness(12) }; Content=root;
        var top=new StackPanel(); DockPanel.SetDock(top,Dock.Top); root.Children.Add(top);
        var header = new Grid { Margin = new Thickness(0,0,0,8) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        top.Children.Add(header);
        var counts=new StackPanel { Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center }; Grid.SetColumn(counts,1); header.Children.Add(counts);
        var identity=new StackPanel(); header.Children.Add(identity); identity.Children.Add(_heading); identity.Children.Add(_info); top.Children.Add(_notice);
        void CountCard(string label,TextBlock value,SolidColorBrush color) { var panel=new StackPanel(); panel.Children.Add(new TextBlock { Text=label }); value.FontSize=20;value.FontWeight=FontWeights.SemiBold;value.Foreground=color;panel.Children.Add(value); counts.Children.Add(new System.Windows.Controls.Border { Background=Paint(241,245,249),CornerRadius=new CornerRadius(5),Padding=new Thickness(12,6,12,6),Margin=new Thickness(0,0,8,0),MinWidth=105,Child=panel }); }
        CountCard("전체 항목",_total,Paint(30,58,95)); CountCard("AI 교정 항목",_corrected,Paint(37,99,235)); CountCard("사용자 수정 항목",_modified,Paint(21,128,61));
        var filters=new DockPanel { Margin=new Thickness(0,4,0,8) }; top.Children.Add(filters);
        var filterPanel=new StackPanel { Orientation=Orientation.Horizontal }; DockPanel.SetDock(filterPanel,Dock.Right); filters.Children.Add(filterPanel);
        filterPanel.Children.Add(new TextBlock { Text="조회 필터  ",VerticalAlignment=VerticalAlignment.Center }); filterPanel.Children.Add(_filter);
        foreach(var label in new[]{"기본정보","구성성분"}) _category.Items.Add(new TabItem { Header=label, MinWidth=110, Padding=new Thickness(14,7,14,7) });
        filters.Children.Add(_category);
        foreach(var label in new[]{"전체","AI 교정","사용자 수정","변경 항목"}) _filter.Items.Add(label);
        var bottom=new DockPanel(); DockPanel.SetDock(bottom,Dock.Bottom); root.Children.Add(bottom);
        var actions=new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=System.Windows.HorizontalAlignment.Right }; DockPanel.SetDock(actions,Dock.Right); bottom.Children.Add(actions);
        _export=Action("Excel 출력",()=>Export()); actions.Children.Add(_export); if(batchExport!=null) actions.Children.Add(Action("전체 Excel + JSON",batchExport)); actions.Children.Add(Action("닫기",Close)); bottom.Children.Add(_status);
        var body=new Grid(); root.Children.Add(body);
        body.RowDefinitions.Add(new RowDefinition { Height=new GridLength(65,GridUnitType.Star),MinHeight=120 }); body.RowDefinitions.Add(new RowDefinition { Height=new GridLength(5) }); body.RowDefinitions.Add(new RowDefinition { Height=new GridLength(35,GridUnitType.Star),MinHeight=150 });
        Setup(_values,("항목명 / 성분","Item",1),("최초 추출값","Ai",2),("AI 교정값","Correction",2),("사용자 수정값","User",2)); body.Children.Add(_values);
        var split=new GridSplitter { HorizontalAlignment=System.Windows.HorizontalAlignment.Stretch, Background=System.Windows.Media.Brushes.LightGray, ResizeDirection=GridResizeDirection.Rows }; Grid.SetRow(split,1); body.Children.Add(split);
        var historyToggle=new System.Windows.Controls.Expander { Header="AI 교정학습 적용 내역", IsExpanded=true, FontWeight=FontWeights.SemiBold }; Grid.SetRow(historyToggle,2); body.Children.Add(historyToggle);
        var lower=new Grid(); historyToggle.Content=lower; lower.RowDefinitions.Add(new RowDefinition { Height=new GridLength(2,GridUnitType.Star) }); lower.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star) });
        Setup(_steps,("순서","Sequence",0.4),("지침명","Rule",1.2),("버전","Version",0.4),("유형","Kind",1),("교정 전","Before",2),("교정 후","After",2),("결과","Status",0.7)); lower.Children.Add(_steps);
        var instructionToggle=new System.Windows.Controls.Expander { Header="자연어 지침 상세",IsExpanded=true,Margin=new Thickness(0,6,0,0),Content=_instruction }; Grid.SetRow(instructionToggle,1); lower.Children.Add(instructionToggle); _instruction.Padding=new Thickness(8);
        GridLength historyHeight=body.RowDefinitions[2].Height, instructionHeight=lower.RowDefinitions[1].Height;
        historyToggle.Collapsed+=(_,e)=>{if(!ReferenceEquals(e.Source,historyToggle)) return;historyHeight=body.RowDefinitions[2].Height;body.RowDefinitions[2].MinHeight=0;body.RowDefinitions[2].Height=GridLength.Auto;split.Visibility=Visibility.Collapsed;body.RowDefinitions[1].Height=new GridLength(0);};
        historyToggle.Expanded+=(_,e)=>{if(!ReferenceEquals(e.Source,historyToggle)) return;body.RowDefinitions[2].Height=historyHeight;body.RowDefinitions[2].MinHeight=150;split.Visibility=Visibility.Visible;body.RowDefinitions[1].Height=new GridLength(5);};
        instructionToggle.Collapsed+=(_,_)=>{instructionHeight=lower.RowDefinitions[1].Height;lower.RowDefinitions[1].Height=GridLength.Auto;};
        instructionToggle.Expanded+=(_,_)=>lower.RowDefinitions[1].Height=instructionHeight;
        _values.SelectionChanged+=(_,_)=> { _steps.ItemsSource=(_values.SelectedItem is DisplayRow d ? Steps(d.Source) : Enumerable.Empty<StepRow>()).ToList(); _steps.SelectedIndex=0; _instruction.Text=(_steps.SelectedItem as StepRow)?.Instruction ?? "적용 이력 없음"; };
        root.SizeChanged += (_, _) =>
        {
            bool compact = root.ActualWidth < 980;
            Grid.SetRow(counts,compact ? 1 : 0);
            Grid.SetColumn(counts,compact ? 0 : 1);
            counts.Margin = compact ? new Thickness(0,4,0,0) : new Thickness(8,0,0,0);
            _filter.Width = compact ? 120 : 150;
            foreach (Button button in actions.Children)
            {
                button.MinWidth = compact ? 80 : 100;
                button.Margin = compact ? new Thickness(2) : new Thickness(4);
            }
            if (historyToggle.IsExpanded)
                body.RowDefinitions[2].MinHeight = root.ActualHeight < 650 ? 110 : 150;
        };
        _steps.SelectionChanged+=(_,_)=>_instruction.Text=(_steps.SelectedItem as StepRow)?.Instruction ?? "적용 이력 없음";
        _category.SelectionChanged+=(_,e)=>{if(ReferenceEquals(e.Source,_category)) Refresh();}; _filter.SelectionChanged+=(_,_)=>Refresh(); _category.SelectedIndex=0; _filter.SelectedIndex=0; SetData(memory,title,stored,materialType,analysisTime,gracoCode);
    }
    private IEnumerable<StepRow> Steps(MsdsUnifiedMemory.Row r)=>_memory.NaturalHistory.Where(s=>ReferenceEquals(s.Target,r)).Select(s=>new StepRow(s.Sequence,_ruleNames.TryGetValue((s.RuleId,s.Version),out var name)?name:s.RuleId,s.Version,s.Kind,s.Before,s.After,ResultLabel(s),s.Instruction));
    private void Refresh() {
        _values.Columns[0].Header = ItemHeader;
        var previous=(_values.SelectedItem as DisplayRow)?.Source;
        var rows=_memory.Rows.Where(r=>r.Category==(_category.SelectedIndex==1?"구성성분":"기본정보")).ToList();
        _total.Text=$"{rows.Count} 건"; _corrected.Text=$"{rows.Count(r=>r.ValueCorrection!=null)} 건"; _modified.Text=$"{rows.Count(r=>r.ValueMod!=null)} 건";
        _status.Text=$"전체 {rows.Count} / AI 교정 {rows.Count(r=>r.ValueCorrection!=null)} / 사용자 수정 {rows.Count(r=>r.ValueMod!=null)}";
        _values.ItemsSource=rows.Where(r=>_filter.SelectedIndex switch {1=>r.ValueCorrection!=null,2=>r.ValueMod!=null,3=>r.ValueCorrection!=null||r.ValueMod!=null,_=>true}).Select(r=>new DisplayRow(r,r.Category=="구성성분"?$"{Show(r.CasNumber)} / {r.FieldName}":r.FieldName,r.ValueAi,r.ValueCorrection??"",r.ValueMod??"",r.FinalValue)).ToList(); var list=(List<DisplayRow>)_values.ItemsSource; _values.SelectedItem=list.FirstOrDefault(x=>previous!=null && x.Source.FieldCode==previous.FieldCode && x.Source.Order==previous.Order && x.Source.CasNumber==previous.CasNumber)??list.FirstOrDefault();
    }
    private static string ResultLabel(MsdsUnifiedMemory.NaturalStep step) => step.Status switch
    {
        "APPLIED" => "교정 적용",
        "UNCHANGED" => "변경 없음",
        "SKIPPED" => string.IsNullOrWhiteSpace(step.Before) ? "조건 불충족 (값 없음)" : "조건 불충족 (값 있음)",
        "FAILED" => "교정 실패",
        _ => step.Status
    };
    private void LoadRuleNames() {
        try {
            using var db=new SqliteConnection(DatabaseManager.GetConnectionString("msds")); db.Open();
            using var cmd=db.CreateCommand(); cmd.CommandText="SELECT ai_learning_id,ai_learning_version,ai_learning_name FROM ai_learning_mstr";
            using var reader=cmd.ExecuteReader(); while(reader.Read()) if(!reader.IsDBNull(2)&&!string.IsNullOrWhiteSpace(reader.GetString(2))) _ruleNames[(reader.GetString(0),Convert.ToInt32(reader.GetValue(1)))]=reader.GetString(2);
        } catch(SqliteException) { /* Keep historical rule ID if name lookup is unavailable. */ }
    }
    private void Export() {
        try {
            BeforeExport?.Invoke();
            if(!_hasData) return;
            string file = $"QCS_MSDS_Review_{DateTime.Now:yyyyMMdd_HHmmss_fffffff}.xlsx";
            var rows=new List<string[]> {new[]{"MSDS ID","구분","순번","CAS No.","성분명","항목","최초 추출값","AI 교정값","사용자 수정값","최종값"}};
            rows.AddRange(_memory.Rows.OrderBy(r=>r.Category=="기본정보"?0:1).Select(r=>new[]{r.MsdsId,r.Category,r.Order?.ToString()??"",r.CasNumber,r.IngredientName,r.FieldName,r.ValueAi,r.ValueCorrection??"",r.ValueMod??"",r.FinalValue}));
            var output = QcsExportPaths.Save("AI_ANALYSIS_RESULTS", file, path =>
                MsdsAnalysisExcelWriter.SaveSheets(path, new (string Name, IReadOnlyList<string[]> Rows)[] { ("분석결과", rows) }));
            _status.Text = output.IsLocal ? $"로컬 저장: {output.Path}" : $"Excel 저장: {output.Path}";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(output.Path) { UseShellExecute = true });
        } catch(Exception ex) { _status.Text="Excel 출력 실패: "+ex.Message; }
    }
    internal static MsdsUnifiedMemory ReadStored(SqliteConnection db,long id,DataRow master,DataTable? details) {
        var m=new MsdsUnifiedMemory();
        string S(DataRow r,string k)=>r.Table.Columns.Contains(k)&&!r.IsNull(k)?Convert.ToString(r[k])??"":"";
        void Add(DataRow r,string dbField,string code,string label,int? order=null) {
            var row=new MsdsUnifiedMemory.Row { MsdsId=id.ToString(),Category=order.HasValue?"구성성분":"기본정보",Order=order,FieldCode=code,FieldName=label,ValueAi=S(r,dbField+"_ai"),CasNumber=order.HasValue?S(r,"msds_comp_cas_no"):"",IngredientName=order.HasValue?S(r,"msds_comp_name"):"" };
            string final=r.Table.Columns.Contains(dbField+"_mod")&&!r.IsNull(dbField+"_mod")?S(r,dbField+"_mod"):row.ValueAi;
            m.Rows.Add(row);
            using var cmd=db.CreateCommand(); cmd.CommandText="SELECT d.ai_applyd_learning_id,d.ai_applyd_learning_version,d.ai_applyd_instruction,d.ai_applyd_rule_type,d.ai_applyd_sequence,d.ai_applyd_value_before,d.ai_applyd_value_after,d.ai_applyd_status,d.ai_applyd_value_ai FROM ai_applyd_det d JOIN ai_apply_mstr a ON a.ai_apply_id=d.ai_applyd_apply_id WHERE a.ai_apply_msds_id=$id AND d.ai_applyd_target_item_key=$key AND COALESCE(d.ai_applyd_cas_no,'')=$cas AND COALESCE(d.ai_applyd_ingredient_name,'')=$name ORDER BY a.ai_apply_id,d.ai_applyd_execution_order"; cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$key",$"{row.Category}:{row.Order}:{code}");cmd.Parameters.AddWithValue("$cas",row.CasNumber);cmd.Parameters.AddWithValue("$name",row.IngredientName);
            using var reader=cmd.ExecuteReader(); string T(int i)=>reader.IsDBNull(i)?"":Convert.ToString(reader.GetValue(i))??"";
            while(reader.Read()) { row.ValueAi=T(8); var step=new MsdsUnifiedMemory.NaturalStep(row,T(0),int.TryParse(T(1),out var ver)?ver:0,T(2),T(3),int.TryParse(T(4),out var seq)?seq:0,T(5),T(6),null,"",T(7),null,DateTime.MinValue); m.NaturalHistory.Add(step); if(step.Status=="APPLIED") {row.ValueCorrection=step.After;row.AiApplied=true;} }
            if(final!=(row.ValueCorrection??row.ValueAi)) row.ValueMod=final;
        }
        foreach(var (column,code,label) in new[]{("moel_submission_no","MoelSubmissionBox","노동부 제출번호"),("product_name","ProductNameBox","제품명"),("supplier_name","SupplierNameBox","공급업체"),("supplier_phone","PhoneBox","전화"),("supplier_address","AddressBox","주소"),("revision_date","RevisionDateBox","개정일"),("revision_no","RevisionNumberBox","개정번호"),("h_codes","HazardCodesBox","유해·위험 문구"),("corrosive_flag","CorrosivityBox","부식성"),("toxicity","ToxicityBox","독성치"),("ppe","PersonalProtectionBox","개인보호구"),("lel","LelBox","LEL"),("uel","UelBox","UEL"),("flash_point","FlashPointBox","인화점"),("autoignition_temp","AutoIgnitionBox","자연발화점"),("vapor_pressure","VaporPressureBox","증기압"),("un_number","UnNumberBox","UN 번호"),("molecular_formula","MolecularFormulaBox","분자식"),("physical_state","PhysicalStateBox","물리적 상태"),("hazardous_goods_class","HazardousGoodsBox","위험물 분류"),("reactivity","ReactivityBox","반응성")}) Add(master,"msds_"+column,code,label);
        if(details!=null) foreach(DataRow r in details.Rows) if(r.RowState!=DataRowState.Deleted&&int.TryParse(S(r,"msds_comp_sort_order"),out var order)) for(int i=0;i<MsdsUnifiedMemory.IngredientCodes.Length;i++) Add(r,"msds_comp_"+MsdsUnifiedMemory.IngredientCodes[i],MsdsUnifiedMemory.IngredientCodes[i],MsdsUnifiedMemory.IngredientNames[i],order);
        return m;
    }
}
