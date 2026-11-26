using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Wpf;
using DataGrid = System.Windows.Controls.DataGrid;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using Button = System.Windows.Controls.Button;
using Binding = System.Windows.Data.Binding;
using Style = System.Windows.Style;
using Brushes = System.Windows.Media.Brushes;

namespace QCS;

internal sealed class MsdsApplicationCasesWindow : Window
{
    public sealed record Case(string Id,string Graco,string Product,string Material,string Date,string Path,string Hash);
    public sealed record Detail(string Cas,string Ingredient,string Field,string Before,string After,string Final,string Status,string Page,string Evidence,string Error,string Instruction,string Version,string Scope) { public string CasDisplay => string.IsNullOrEmpty(Cas) ? Ingredient : $"{Cas}({Ingredient})"; public string StatusLabel => FormatStatus(Status); }
    internal static string FormatStatus(string status) => status switch
    {
        "APPLIED" => "변경됨",
        "UNCHANGED" => "변경 없음",
        "SKIPPED" => "조건 불충족",
        "INSUFFICIENT" => "근거·판단 부족",
        "FAILED" => "실패",
        _ => status
    };
    private readonly string _rule;
    private readonly DataGrid _cases = MakeGrid();
    private readonly Dictionary<string,List<Detail>> _detailsByCase = new();
    private readonly Dictionary<string,List<Detail>> _visible = new();
    private Case? _activeCase; private Detail? _activeDetail;
    private readonly TextBox _search = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _before = new() { TextWrapping = TextWrapping.Wrap }, _after = new() { TextWrapping = TextWrapping.Wrap }, _info = new() { TextWrapping = TextWrapping.Wrap };
    private readonly WebView2 _pdf = new();
    private readonly List<Case> _all = new();
    private readonly List<string> _temps = new();
    private bool _closed;
    private DataGrid? _selectedDetailGrid;
    private readonly Dictionary<string,DataGrid> _detailGrids = new();
    private readonly HashSet<string> _expanded = new();
    private int _request;
    private static DataGrid MakeGrid() { var grid = new DataGrid { RowHeight=26, MinRowHeight=26, AutoGenerateColumns=false, IsReadOnly=true, CanUserAddRows=false, SelectionMode=System.Windows.Controls.DataGridSelectionMode.Single, Style=System.Windows.Application.Current.TryFindResource("QcsGrid") as Style, RowStyle=System.Windows.Application.Current.TryFindResource("QcsGridRow") as Style };
        var row = new Style(typeof(System.Windows.Controls.DataGridRow), grid.RowStyle); row.Setters.Add(new System.Windows.Setter(System.Windows.FrameworkElement.MinHeightProperty,26d));
        var selected = new System.Windows.Trigger { Property=System.Windows.Controls.DataGridRow.IsSelectedProperty, Value=true };
        selected.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty, Brushes.DodgerBlue));
        selected.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty, Brushes.White));
        row.Triggers.Add(selected); grid.RowStyle=row;
        var cell = new Style(typeof(System.Windows.Controls.DataGridCell), grid.CellStyle);
        var cs = new System.Windows.Trigger { Property=System.Windows.Controls.DataGridCell.IsSelectedProperty, Value=true };
        cs.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty, Brushes.DodgerBlue));
        cs.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty, Brushes.White));
        cell.Triggers.Add(cs); grid.CellStyle=cell; return grid; }
    private static void Column(DataGrid grid,string label,string path,double width) => grid.Columns.Add(new System.Windows.Controls.DataGridTextColumn { Header=label, Binding=new Binding(path), Width=new System.Windows.Controls.DataGridLength(width,System.Windows.Controls.DataGridLengthUnitType.Star), ElementStyle=CellText(path) });
    // RowDetails can be measured with an unconstrained horizontal size.
    // Give the nested grid a finite width derived from the outer grid viewport.
    private void BindDetailWidth(DataGrid detail, System.Windows.Controls.ContentControl host)
    {
        host.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch;
        detail.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        System.Windows.SizeChangedEventHandler resize = (_, _) => UpdateWidth();
        void UpdateWidth()
        {
            double width = _cases.ActualWidth - detail.Margin.Left - detail.Margin.Right
                - System.Windows.SystemParameters.VerticalScrollBarWidth - 8;
            if (width > 0 && !double.IsInfinity(width)) detail.Width = width;
        }
        detail.Loaded += (_, _) => { _cases.SizeChanged -= resize; _cases.SizeChanged += resize; UpdateWidth(); };
        detail.Unloaded += (_, _) => _cases.SizeChanged -= resize;
        UpdateWidth();
    }
    private static Style CellText(string path) { var style=new Style(typeof(TextBlock),System.Windows.Application.Current.TryFindResource("QcsGridText") as Style); style.Setters.Add(new System.Windows.Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis)); style.Setters.Add(new System.Windows.Setter(System.Windows.FrameworkElement.ToolTipProperty,new Binding(path))); return style; }
    private static SqliteConnection Open() { var db=new SqliteConnection(DatabaseManager.GetConnectionString("msds")); db.Open(); return db; }
    private static string Read(SqliteDataReader r,int i) => r.IsDBNull(i)?"":Convert.ToString(r.GetValue(i))??"";
    private static void Top(DockPanel p, UIElement e) { DockPanel.SetDock(e,Dock.Top); p.Children.Add(e); }
    public MsdsApplicationCasesWindow(string rule)
    {
        _rule=rule; Title="AI 교정학습 적용사례"; WindowState=System.Windows.WindowState.Maximized;
        var root=new DockPanel { Margin=new Thickness(12) }; Content=root;
        DockPanel.SetDock(_status,Dock.Bottom); root.Children.Add(_status);
        var grid=new Grid(); root.Children.Add(grid);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(55,GridUnitType.Star), MinWidth=300 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(5) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(45,GridUnitType.Star), MinWidth=250 });
        var verticalSplitter=new System.Windows.Controls.GridSplitter { Width=5, HorizontalAlignment=System.Windows.HorizontalAlignment.Stretch, VerticalAlignment=System.Windows.VerticalAlignment.Stretch, Background=Brushes.LightGray, ResizeDirection=System.Windows.Controls.GridResizeDirection.Columns, ResizeBehavior=System.Windows.Controls.GridResizeBehavior.PreviousAndNext }; Grid.SetColumn(verticalSplitter,1); Grid.SetRowSpan(verticalSplitter,2); grid.Children.Add(verticalSplitter);
        grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star) });
        var left=new DockPanel { Margin=new Thickness(0,0,8,6) }; grid.Children.Add(left);
        Top(left,new TextBlock { Text="통합검색 (공백 = AND)", FontWeight=System.Windows.FontWeights.SemiBold }); _search.Height=30; left.Children.Add(_search);
        var middle=new Grid { Margin=new Thickness(0,0,8,0) }; Grid.SetRow(middle,1); grid.Children.Add(middle);
        middle.RowDefinitions.Add(new RowDefinition { Height=new GridLength(4,GridUnitType.Star), MinHeight=80 }); middle.RowDefinitions.Add(new RowDefinition { Height=new GridLength(5) }); middle.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star), MinHeight=70 });
        var splitter=new System.Windows.Controls.GridSplitter { Height=5, HorizontalAlignment=System.Windows.HorizontalAlignment.Stretch, VerticalAlignment=System.Windows.VerticalAlignment.Center, Background=Brushes.LightGray, ResizeDirection=System.Windows.Controls.GridResizeDirection.Rows, ResizeBehavior=System.Windows.Controls.GridResizeBehavior.PreviousAndNext }; Grid.SetRow(splitter,1); middle.Children.Add(splitter);
        middle.Children.Add(_cases);
        var toggle = new System.Windows.FrameworkElementFactory(typeof(Button));
        var toggleStyle=new Style(typeof(Button)); toggleStyle.Setters.Add(new System.Windows.Setter(System.Windows.Controls.ContentControl.ContentProperty,"▶")); var openTrigger=new System.Windows.DataTrigger { Binding=new Binding("DetailsVisibility") { RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor,typeof(System.Windows.Controls.DataGridRow),1) }, Value=Visibility.Visible }; openTrigger.Setters.Add(new System.Windows.Setter(System.Windows.Controls.ContentControl.ContentProperty,"▼")); toggleStyle.Triggers.Add(openTrigger); toggle.SetValue(System.Windows.FrameworkElement.StyleProperty,toggleStyle);
        toggle.AddHandler(Button.ClickEvent,new RoutedEventHandler((sender,e)=> { if(sender is Button button && button.DataContext is Case c && _cases.ItemContainerGenerator.ContainerFromItem(c) is System.Windows.Controls.DataGridRow row) { bool open=row.DetailsVisibility!=Visibility.Visible; if(open)_expanded.Add(c.Id); else _expanded.Remove(c.Id); row.DetailsVisibility=open?Visibility.Visible:Visibility.Collapsed; e.Handled=true; } }));
        _cases.Columns.Add(new System.Windows.Controls.DataGridTemplateColumn { Header="", Width=32, CellTemplate=new System.Windows.DataTemplate { VisualTree=toggle } });
        Column(_cases,"ID","Id",100); Column(_cases,"GracoCode","Graco",90); Column(_cases,"제품/원료","Material",85); Column(_cases,"제품명","Product",220); Column(_cases,"날짜","Date",160);
        _cases.RowDetailsVisibilityMode=System.Windows.Controls.DataGridRowDetailsVisibilityMode.Collapsed;
        _cases.AreRowDetailsFrozen=true;
        var host=new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ContentControl));
        _cases.RowDetailsTemplate=new System.Windows.DataTemplate { VisualTree=host };
        _cases.LoadingRowDetails+=(_,e)=> { if(e.Row.Item is Case c && e.DetailsElement is System.Windows.Controls.ContentControl content && _visible.TryGetValue(c.Id,out var rows)) { var detail=MakeGrid(); detail.Margin=new Thickness(12,2,2,4); detail.MaxHeight=350; detail.MinHeight=80; Column(detail,"항목명","Field",15); Column(detail,"CAS No(성분명)","CasDisplay",25); Column(detail,"변경전","Before",30); Column(detail,"변경후","After",30); Column(detail,"결과","StatusLabel",20); BindDetailWidth(detail,content); detail.ItemsSource=rows; _detailGrids[c.Id]=detail; detail.SelectionChanged+=async (_,args)=> { args.Handled=true; if(detail.SelectedItem is Detail d) { if(_selectedDetailGrid!=null && _selectedDetailGrid!=detail) _selectedDetailGrid.SelectedItem=null; _selectedDetailGrid=detail; _activeCase=c; _activeDetail=d; _cases.SelectedItem=c; await Preview(); } }; content.Content=detail; } };
        _cases.LoadingRow+=(_,e)=>e.Row.DetailsVisibility=e.Row.Item is Case c && _expanded.Contains(c.Id)?Visibility.Visible:Visibility.Collapsed;
        _cases.UnloadingRowDetails+=(_,e)=> { if(e.Row.Item is Case c) _detailGrids.Remove(c.Id); };
        _cases.SelectionChanged+=(_,e)=> { if(e.OriginalSource!=_cases)return; if(_cases.SelectedItem is Case c && _activeCase?.Id!=c.Id && _selectedDetailGrid!=null && (!_detailGrids.TryGetValue(c.Id,out var g)||g!=_selectedDetailGrid)) { _selectedDetailGrid.SelectedItem=null; _selectedDetailGrid=null; ++_request; _activeCase=null; _activeDetail=null; _before.Text=_after.Text=_info.Text=""; _pdf.CoreWebView2?.Navigate("about:blank"); } };
        var values=new StackPanel { Margin=new Thickness(4) }; var scroll=new ScrollViewer { Content=values, VerticalScrollBarVisibility=System.Windows.Controls.ScrollBarVisibility.Auto }; Grid.SetRow(scroll,2); middle.Children.Add(scroll);
        foreach(var pair in new[]{("교정 전",_before),("교정 후",_after),("적용 정보",_info)}) { values.Children.Add(new TextBlock { Text=pair.Item1, FontWeight=System.Windows.FontWeights.SemiBold, Margin=new Thickness(0,8,0,4) }); values.Children.Add(pair.Item2); }
        Grid.SetColumn(_pdf,2); Grid.SetRowSpan(_pdf,2); grid.Children.Add(_pdf);
        _search.TextChanged+=(_,_)=>Filter(); 
        Loaded+=(_,_)=>LoadCases(); Closed+=(_,_)=>{_closed=true; _pdf.Dispose(); foreach(var p in _temps) try {File.Delete(p);} catch(IOException){} catch(UnauthorizedAccessException){} };
    }
    private void LoadCases()
    {
        try { using var db=Open(); using var cmd=db.CreateCommand(); cmd.CommandText="SELECT a.ai_apply_id,a.ai_apply_graco_code,a.ai_apply_product_name,a.ai_apply_material_type,a.ai_apply_applied_at,a.ai_apply_pdf_path,a.ai_apply_pdf_hash FROM ai_apply_mstr a WHERE EXISTS (SELECT 1 FROM ai_applyd_det d WHERE d.ai_applyd_apply_id=a.ai_apply_id AND d.ai_applyd_learning_id IN (SELECT $rule UNION SELECT ai_instruction_versionh_id FROM ai_instruction_versionh_hist WHERE ai_instruction_versionh_family_id=$rule)) ORDER BY a.ai_apply_applied_at DESC"; cmd.Parameters.AddWithValue("$rule",_rule); using var r=cmd.ExecuteReader(); _all.Clear(); while(r.Read()) _all.Add(new Case(Read(r,0),Read(r,1),Read(r,2),Read(r,3),Read(r,4),Read(r,5),Read(r,6))); r.Close(); _detailsByCase.Clear(); foreach(var c in _all) _detailsByCase[c.Id]=LoadDetails(c); Filter(); } catch(Exception ex){_status.Text="조회 실패: "+ex.Message;}
    }
    private void Filter()
    {
        ++_request; _activeCase=null; _activeDetail=null; _before.Text=_after.Text=_info.Text=""; _pdf.CoreWebView2?.Navigate("about:blank");
        _selectedDetailGrid=null; _detailGrids.Clear(); _expanded.Clear(); _cases.ItemsSource=null; _visible.Clear();
        var terms=_search.Text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        foreach(var c in _all) { string master=$"{c.Id} {c.Graco} {c.Product} {c.Material} {c.Date}"; var rows=_detailsByCase[c.Id].Where(d=>terms.All(t=>$"{master} {d.Field} {d.Cas} {d.Ingredient} {d.Before} {d.After} {d.StatusLabel} {d.Error}".Contains(t,StringComparison.OrdinalIgnoreCase))).ToList(); if(rows.Count>0)_visible[c.Id]=rows; }
        if(terms.Length>0) foreach(var id in _visible.Keys) _expanded.Add(id);
        _cases.ItemsSource=_all.Where(c=>_visible.ContainsKey(c.Id)).ToList(); _status.Text=$"적용 이력 {_visible.Count}건 / 교정 {_visible.Values.Sum(x=>x.Count)}건";
    }
    private List<Detail> LoadDetails(Case c)
    {
        using var db=Open(); using var cmd=db.CreateCommand(); cmd.CommandText="SELECT ai_applyd_cas_no,ai_applyd_ingredient_name,ai_applyd_field_code,ai_applyd_value_before,ai_applyd_value_after,ai_applyd_value_final,CASE WHEN ai_applyd_error LIKE '[SKIPPED] %' THEN 'SKIPPED' WHEN ai_applyd_error LIKE '[INSUFFICIENT] %' THEN 'INSUFFICIENT' ELSE ai_applyd_status END,ai_applyd_source_page,ai_applyd_source_text,CASE WHEN ai_applyd_error LIKE '[SKIPPED] %' OR ai_applyd_error LIKE '[INSUFFICIENT] %' THEN substr(ai_applyd_error,instr(ai_applyd_error,'] ')+2) ELSE ai_applyd_error END,ai_applyd_instruction,ai_applyd_learning_version, CASE WHEN m.ai_learning_version=ai_applyd_learning_version THEN CASE m.ai_learning_scope WHEN 'GROUP' THEN 'GROUP · ' || m.ai_learning_material_type WHEN 'PRODUCT' THEN 'PRODUCT · ' || COALESCE(m.ai_learning_graco_code,m.ai_learning_supplier_name || ' / ' || m.ai_learning_product_name) WHEN 'SUPPLIER' THEN 'SUPPLIER · ' || m.ai_learning_supplier_name ELSE m.ai_learning_scope END ELSE '' END FROM ai_applyd_det LEFT JOIN ai_learning_mstr m ON m.ai_learning_id=ai_applyd_learning_id WHERE ai_applyd_apply_id=$id AND ai_applyd_learning_id IN (SELECT $rule UNION SELECT ai_instruction_versionh_id FROM ai_instruction_versionh_hist WHERE ai_instruction_versionh_family_id=$rule) ORDER BY ai_applyd_execution_order,ai_applyd_id"; cmd.Parameters.AddWithValue("$id",c.Id); cmd.Parameters.AddWithValue("$rule",_rule); using var r=cmd.ExecuteReader(); var rows=new List<Detail>(); while(r.Read())rows.Add(new Detail(Read(r,0),Read(r,1),Read(r,2),Read(r,3),Read(r,4),Read(r,5),Read(r,6),Read(r,7),Read(r,8),Read(r,9),Read(r,10),Read(r,11),Read(r,12))); return rows;
    }
    private static string Compact(string value) => new string(value.Where(ch=>!char.IsWhiteSpace(ch)).Select(char.ToUpperInvariant).ToArray());
    private async System.Threading.Tasks.Task Preview()
    {
        int request=++_request;
        _pdf.CoreWebView2?.Navigate("about:blank");
        if(_activeCase is not Case c || _activeDetail is not Detail d)return;
        _before.Text=d.Before; _after.Text=d.After; 
        _info.Text=$"최종값: {d.Final}\n상태: {d.StatusLabel}\n사유: {(string.IsNullOrWhiteSpace(d.Error) ? "—" : d.Error)}\n적용 범위: {(string.IsNullOrEmpty(d.Scope) ? "기록 없음" : d.Scope)} · 버전 v{d.Version}\n지침: {d.Instruction}\n원문 페이지: {d.Page}\n근거: {d.Evidence}";
        try { if(!File.Exists(c.Path)){_pdf.CoreWebView2?.Navigate("about:blank"); _status.Text="PDF 파일을 찾을 수 없습니다: "+c.Path; return;}
            int page=int.TryParse(d.Page,out int n)&&n>0?n:1; string path=c.Path; bool marked=false; double scrollY=0;
            string evidence=string.IsNullOrWhiteSpace(d.Evidence)?d.Before:d.Evidence;
            if(!string.IsNullOrWhiteSpace(c.Hash)) { string temp=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"QCS_"+Guid.NewGuid().ToString("N")+".pdf"); _temps.Add(temp); using(var f=File.OpenRead(c.Path)) if(string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f)),c.Hash,StringComparison.OrdinalIgnoreCase)) try { using var parsed=UglyToad.PdfPig.PdfDocument.Open(c.Path); if(MsdsPdfHighlightPreview.IsIngredientField(d.Field) && !string.IsNullOrWhiteSpace(d.Ingredient+d.Cas)) { marked=MsdsPdfHighlightPreview.TryIngredient(c.Path,temp,d.Field,d.Cas,d.Ingredient,d.Before,out int found,out double y0); if(found>0)page=found; if(marked)scrollY=y0; } else { var hits=new List<int>(); var candidates=new[]{evidence.Trim()}.Where(x=>Compact(x).Length>=6); foreach(var candidate in candidates) { hits.Clear(); string target=Compact(candidate); for(int i=1;i<=parsed.NumberOfPages;i++) if(Compact(string.Concat(parsed.GetPage(i).Letters.Select(l=>l.Value))).Contains(target,StringComparison.Ordinal)) hits.Add(i); if(hits.Count==1 && MsdsPdfHighlightPreview.TryCreate(c.Path,temp,hits[0],candidate,out double y)) { page=hits[0]; marked=true; scrollY=y; break; } } } }catch{marked=false;} if(marked)path=temp; }
            await _pdf.EnsureCoreWebView2Async(); if(_closed||request!=_request)return; var full=System.IO.Path.GetFullPath(path); var uri=new Uri(full); string address=uri.AbsoluteUri; if(!uri.IsUnc) { _pdf.CoreWebView2.SetVirtualHostNameToFolderMapping("qcs-case-preview.local",System.IO.Path.GetDirectoryName(full)!,Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Deny); address="https://qcs-case-preview.local/"+Uri.EscapeDataString(System.IO.Path.GetFileName(full)); } _pdf.CoreWebView2.Navigate(address+"#page="+page+(marked?"&zoom=100,0,"+Math.Max(0,(int)(scrollY-60)):"")); _status.Text=marked?"원문 근거 강조":"원문 표시 (강조 가능한 근거 없음)";
        }catch(Exception ex){if(!_closed&&request==_request)_status.Text="PDF 조회 실패: "+ex.Message;}
    }
}
