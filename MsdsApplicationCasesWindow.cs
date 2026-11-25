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
    public sealed record Detail(string Cas,string Ingredient,string Field,string Before,string After,string Final,string Status,string Page,string Evidence,string Error,string Instruction) { public string StatusLabel => Status == "FAILED" ? "실패" : Status == "APPLIED" ? "성공" : Status == "UNCHANGED" ? "변경 없음" : Status; }
    private readonly string _rule;
    private readonly DataGrid _cases = MakeGrid(), _details = MakeGrid();
    private readonly TextBox _search = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _before = new() { TextWrapping = TextWrapping.Wrap }, _after = new() { TextWrapping = TextWrapping.Wrap }, _info = new() { TextWrapping = TextWrapping.Wrap };
    private readonly WebView2 _pdf = new();
    private readonly List<Case> _all = new();
    private readonly List<string> _temps = new();
    private bool _closed;
    private int _request;
    private static DataGrid MakeGrid() { var grid = new DataGrid { AutoGenerateColumns=false, IsReadOnly=true, CanUserAddRows=false, SelectionMode=System.Windows.Controls.DataGridSelectionMode.Single, Style=System.Windows.Application.Current.TryFindResource("QcsGrid") as Style, RowStyle=System.Windows.Application.Current.TryFindResource("QcsGridRow") as Style };
        var row = new Style(typeof(System.Windows.Controls.DataGridRow), grid.RowStyle);
        var selected = new System.Windows.Trigger { Property=System.Windows.Controls.DataGridRow.IsSelectedProperty, Value=true };
        selected.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty, Brushes.DodgerBlue));
        selected.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty, Brushes.White));
        row.Triggers.Add(selected); grid.RowStyle=row;
        var cell = new Style(typeof(System.Windows.Controls.DataGridCell), grid.CellStyle);
        var cs = new System.Windows.Trigger { Property=System.Windows.Controls.DataGridCell.IsSelectedProperty, Value=true };
        cs.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty, Brushes.DodgerBlue));
        cs.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty, Brushes.White));
        cell.Triggers.Add(cs); grid.CellStyle=cell; return grid; }
    private static void Column(DataGrid grid,string label,string path,double width) => grid.Columns.Add(new System.Windows.Controls.DataGridTextColumn { Header=label, Binding=new Binding(path), Width=new System.Windows.Controls.DataGridLength(width), ElementStyle=System.Windows.Application.Current.TryFindResource("QcsGridText") as Style });
    private static SqliteConnection Open() { var db=new SqliteConnection(DatabaseManager.GetConnectionString("msds")); db.Open(); return db; }
    private static string Read(SqliteDataReader r,int i) => r.IsDBNull(i)?"":Convert.ToString(r.GetValue(i))??"";
    private static void Top(DockPanel p, UIElement e) { DockPanel.SetDock(e,Dock.Top); p.Children.Add(e); }
    public MsdsApplicationCasesWindow(string rule)
    {
        _rule=rule; Title="AI 교정학습 적용사례"; WindowState=System.Windows.WindowState.Maximized;
        var root=new DockPanel { Margin=new Thickness(12) }; Content=root;
        DockPanel.SetDock(_status,Dock.Bottom); root.Children.Add(_status);
        var grid=new Grid(); root.Children.Add(grid);
        foreach(double w in new[]{25d,35d,40d}) grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(w,GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star) });
        var left=new DockPanel { Margin=new Thickness(0,0,8,6) }; grid.Children.Add(left);
        Top(left,new TextBlock { Text="적용 PDF 목록 · 검색 (공백 = AND)", FontWeight=System.Windows.FontWeights.SemiBold }); _search.Height=30; left.Children.Add(_search); Grid.SetRow(_cases,1); _cases.Margin=new Thickness(0,0,8,0); grid.Children.Add(_cases);
        Column(_cases,"GracoCode","Graco",95); Column(_cases,"제품명","Product",220); Column(_cases,"적용일시","Date",145);
        var middle=new Grid { Margin=new Thickness(0,0,8,0) }; Grid.SetColumn(middle,1); Grid.SetRow(middle,1); grid.Children.Add(middle);
        middle.RowDefinitions.Add(new RowDefinition { Height=new GridLength(4,GridUnitType.Star), MinHeight=80 }); middle.RowDefinitions.Add(new RowDefinition { Height=new GridLength(5) }); middle.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star), MinHeight=70 });
        var splitter=new System.Windows.Controls.GridSplitter { Height=5, HorizontalAlignment=System.Windows.HorizontalAlignment.Stretch, VerticalAlignment=System.Windows.VerticalAlignment.Center, Background=Brushes.LightGray, ResizeDirection=System.Windows.Controls.GridResizeDirection.Rows, ResizeBehavior=System.Windows.Controls.GridResizeBehavior.PreviousAndNext }; Grid.SetRow(splitter,1); middle.Children.Add(splitter);
        middle.Children.Add(_details); Column(_details,"CAS No.","Cas",100); Column(_details,"성분명","Ingredient",130); Column(_details,"항목","Field",75); Column(_details,"상태","StatusLabel",90);
        var values=new StackPanel { Margin=new Thickness(4) }; var scroll=new ScrollViewer { Content=values, VerticalScrollBarVisibility=System.Windows.Controls.ScrollBarVisibility.Auto }; Grid.SetRow(scroll,2); middle.Children.Add(scroll);
        foreach(var pair in new[]{("교정 전",_before),("교정 후",_after),("적용 정보",_info)}) { values.Children.Add(new TextBlock { Text=pair.Item1, FontWeight=System.Windows.FontWeights.SemiBold, Margin=new Thickness(0,8,0,4) }); values.Children.Add(pair.Item2); }
        Grid.SetColumn(_pdf,2); Grid.SetRowSpan(_pdf,2); grid.Children.Add(_pdf);
        _search.TextChanged+=(_,_)=>Filter(); _cases.SelectionChanged+=(_,_)=>LoadDetails(); _details.SelectionChanged+=async (_,_)=>await Preview();
        Loaded+=(_,_)=>LoadCases(); Closed+=(_,_)=>{_closed=true; _pdf.Dispose(); foreach(var p in _temps) try {File.Delete(p);} catch(IOException){} catch(UnauthorizedAccessException){} };
    }
    private void LoadCases()
    {
        try { using var db=Open(); using var cmd=db.CreateCommand(); cmd.CommandText="SELECT a.ai_apply_id,a.ai_apply_graco_code,a.ai_apply_product_name,a.ai_apply_material_type,a.ai_apply_applied_at,a.ai_apply_pdf_path,a.ai_apply_pdf_hash FROM ai_apply_mstr a WHERE EXISTS (SELECT 1 FROM ai_applyd_det d WHERE d.ai_applyd_apply_id=a.ai_apply_id AND d.ai_applyd_learning_id=$rule) ORDER BY a.ai_apply_applied_at DESC"; cmd.Parameters.AddWithValue("$rule",_rule); using var r=cmd.ExecuteReader(); _all.Clear(); while(r.Read()) _all.Add(new Case(Read(r,0),Read(r,1),Read(r,2),Read(r,3),Read(r,4),Read(r,5),Read(r,6))); Filter(); } catch(Exception ex){_status.Text="조회 실패: "+ex.Message;}
    }
    private void Filter(){var terms=_search.Text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries); _cases.ItemsSource=_all.Where(c=>terms.All(t=>$"{c.Id} {c.Graco} {c.Product} {c.Material} {c.Date}".Contains(t,StringComparison.OrdinalIgnoreCase))).ToList(); _cases.SelectedIndex=_cases.Items.Count>0?0:-1;}
    private void LoadDetails()
    {
        ++_request; _details.ItemsSource=null; _before.Text=_after.Text=_info.Text=""; _pdf.CoreWebView2?.Navigate("about:blank");
        if(_cases.SelectedItem is not Case c)return;
        try {using var db=Open(); using var cmd=db.CreateCommand(); cmd.CommandText="SELECT ai_applyd_cas_no,ai_applyd_ingredient_name,ai_applyd_field_code,ai_applyd_value_before,ai_applyd_value_after,ai_applyd_value_final,ai_applyd_status,ai_applyd_source_page,ai_applyd_source_text,ai_applyd_error,ai_applyd_instruction FROM ai_applyd_det WHERE ai_applyd_apply_id=$id AND ai_applyd_learning_id=$rule ORDER BY ai_applyd_execution_order,ai_applyd_id"; cmd.Parameters.AddWithValue("$id",c.Id); cmd.Parameters.AddWithValue("$rule",_rule); using var r=cmd.ExecuteReader(); var rows=new List<Detail>(); while(r.Read())rows.Add(new Detail(Read(r,0),Read(r,1),Read(r,2),Read(r,3),Read(r,4),Read(r,5),Read(r,6),Read(r,7),Read(r,8),Read(r,9),Read(r,10))); _details.ItemsSource=rows; _details.SelectedIndex=rows.Count>0?0:-1;}catch(Exception ex){_status.Text="상세 조회 실패: "+ex.Message;}
    }
    private static string Compact(string value) => new string(value.Where(ch=>!char.IsWhiteSpace(ch)).Select(char.ToUpperInvariant).ToArray());
    private async System.Threading.Tasks.Task Preview()
    {
        int request=++_request;
        _pdf.CoreWebView2?.Navigate("about:blank");
        if(_cases.SelectedItem is not Case c || _details.SelectedItem is not Detail d)return;
        _before.Text=d.Before; _after.Text=d.After; 
        _info.Text=$"최종값: {d.Final}\n상태: {d.StatusLabel}\n실패 사유: {(d.Status == "FAILED" ? (string.IsNullOrWhiteSpace(d.Error) ? "기록된 사유 없음" : d.Error) : "해당 없음")}\n지침: {d.Instruction}\n원문 페이지: {d.Page}\n근거: {d.Evidence}";
        try { if(!File.Exists(c.Path)){_pdf.CoreWebView2?.Navigate("about:blank"); _status.Text="PDF 파일을 찾을 수 없습니다: "+c.Path; return;}
            int page=int.TryParse(d.Page,out int n)&&n>0?n:1; string path=c.Path; bool marked=false;
            string evidence=string.IsNullOrWhiteSpace(d.Evidence)?d.Before:d.Evidence;
            if(!string.IsNullOrWhiteSpace(evidence)&&!string.IsNullOrWhiteSpace(c.Hash)) { string temp=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"QCS_"+Guid.NewGuid().ToString("N")+".pdf"); _temps.Add(temp); using(var f=File.OpenRead(c.Path)) if(string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f)),c.Hash,StringComparison.OrdinalIgnoreCase)) try { using var parsed=UglyToad.PdfPig.PdfDocument.Open(c.Path); var hits=new List<int>(); string target=Compact(evidence); if(target.Length>=2) for(int i=1;i<=parsed.NumberOfPages;i++) if(Compact(string.Concat(parsed.GetPage(i).Letters.Select(l=>l.Value))).Contains(target,StringComparison.Ordinal)) hits.Add(i); if(hits.Count==1) { page=hits[0]; marked=MsdsPdfHighlightPreview.TryCreate(c.Path,temp,page,evidence); } }catch{marked=false;} if(marked)path=temp; }
            await _pdf.EnsureCoreWebView2Async(); if(_closed||request!=_request)return; _pdf.CoreWebView2.Navigate(new Uri(System.IO.Path.GetFullPath(path)).AbsoluteUri+"#page="+page); _status.Text=marked?"원문 근거 강조":"원문 표시 (강조 가능한 근거 없음)";
        }catch(Exception ex){if(!_closed&&request==_request)_status.Text="PDF 조회 실패: "+ex.Message;}
    }
}
