using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using UserControl = System.Windows.Controls.UserControl;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;

namespace QCS;
public partial class MsdsLearningManagementView : UserControl
{
    private const string ProgramId = "MsdsLearningManagementView.xaml.cs";
    public sealed record Option(string Key, string Label, string? Tooltip = null);
    public sealed record Usage(string Id, string Method, bool Revised);
    public sealed record Learning(string Id, string When, string Supplier, string Product, string Doc, string FieldKey, string Before, string After, string State, string By, string Path, string Hash, string ChangedBy, string ChangedAt, string Reason, string Evidence, string Context, string Page)
    {
        public List<Usage> Applications { get; } = new();
        public string FieldLabel { get; set; } = FieldKey;
        public string StateLabel => State switch { "APPROVED" => "활성", "INACTIVE" => "비활성", "PENDING" => "대기", _ => State };
        public string WhenDisplay => DateTimeOffset.TryParse(When, out var date) ? date.ToString("yyyy-MM-dd HH:mm:ss") : When;
        public int Uses => Applications.Count;
        public int Revised => Applications.Count(x => x.Revised);
        public double? Rate => Uses == 0 ? null : (double)Revised / Uses;
    }
    // Historical extraction keys remain unchanged in the learning tables.
    private static readonly Dictionary<string,string> LegacyFields = new(StringComparer.OrdinalIgnoreCase) {
        ["ProductNameBox"]="msds_product_name",
        ["SupplierNameBox"]="msds_supplier_name",
        ["GracoCodeBox"]="msds_graco_code",
        ["RevisionDateBox"]="msds_revision_date",
        ["RevisionNumberBox"]="msds_revision_no",
        ["PhoneBox"]="msds_supplier_phone",
        ["AddressBox"]="msds_supplier_address",
        ["UnNumberBox"]="msds_un_number",
        ["HazardCodesBox"]="msds_h_codes",
        ["AutoIgnitionBox"]="msds_autoignition_temp",
        ["VaporPressureBox"]="msds_vapor_pressure",
        ["LelBox"]="msds_lel",
        ["UelBox"]="msds_uel",
        ["FlashPointBox"]="msds_flash_point",
        ["CorrosivityBox"]="msds_corrosive_flag",
        ["PersonalProtectionBox"]="msds_ppe",
        ["MoelSubmissionBox"]="msds_moel_submission_no"
    };
    private static string DbField(string key) => LegacyFields.TryGetValue(key,out var field) ? field : key;
    private string _submittedSearch = "";
    private int _submittedLogic;
    private readonly List<Learning> _all = new();
    private List<Option> _methods = new();
    private bool _busy = true;
    private readonly QcsCodeTooltips _codeTips = new();
    private MainWindow? Owner => Window.GetWindow(this) as MainWindow;
    public MsdsLearningManagementView() { InitializeComponent(); Loaded += (_,_) => Reload(); }
    private void FilterLayoutSizeChanged(object sender,SizeChangedEventArgs e)
    {
        if(StatsPanel==null || FilterPanel==null) return;
        bool compact=e.NewSize.Width<1100;
        FilterColumn.Width=new GridLength(compact?1:1.15,GridUnitType.Star);
        StatsColumn.Width=compact?new GridLength(0):new GridLength(1,GridUnitType.Star);
        Grid.SetColumn(StatsPanel,compact?0:1); Grid.SetRow(StatsPanel,compact?1:0);
        FilterPanel.Margin=new Thickness(0,0,compact?0:8,4);
    }
    private static SqliteConnection Open(string name) { var c = new SqliteConnection(DatabaseManager.GetConnectionString(name)); try { c.Open(); return c; } catch { c.Dispose(); throw; } }
    private static string T(SqliteDataReader r,int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i)) ?? "";
    private bool Allowed() => QcsAuthorizationService.CanAccessProgram(Owner?.GetCurrentUser(), ProgramId);
    private void Status(string s) => Owner?.SetRunStatus(s);
    private void Error(string text, Exception ex) { Status(text + ": " + ex.Message); MessageBox.Show(text + "\n" + ex.Message,"오류",MessageBoxButton.OK,MessageBoxImage.Error); }
    private void Options(ComboBox c, IEnumerable<Option> values) { var old=c.SelectedValue as string; var list=new[]{new Option("","전체")}.Concat(values).ToList(); c.ItemsSource=list; c.SelectedValue=list.Any(x=>x.Key==old) ? old : ""; }
    private void Reload()
    {
        _busy=true;
        try
        {
            if (!Allowed()) { _all.Clear(); Results.ItemsSource=null; Status("AI 교정학습 관리: 접근 제한"); return; }
            var rows=new List<Learning>(); var methods=new List<Option>(); var fields=new List<Option>();
            using(var c=Open("common")) _codeTips.Reload(c);
            using(var c=Open("common")) using(var q=c.CreateCommand()) { q.CommandText="SELECT zx_code_value,zx_code_desc1 FROM zx_code_mstr WHERE zx_code_fldname='AI_SIMILAR_METHOD' ORDER BY zx_code_value"; using var r=q.ExecuteReader(); while(r.Read()) methods.Add(new(T(r,0),T(r,0)+" · "+T(r,1),_codeTips.Get("AI_SIMILAR_METHOD",T(r,0)))); }
            using(var c=Open("common")) using(var q=c.CreateCommand()) { q.CommandText="SELECT zx_code_value,zx_code_desc1 FROM zx_code_mstr WHERE zx_code_fldname='AI_CORRECTION_FIELD' ORDER BY zx_code_value"; using var r=q.ExecuteReader(); while(r.Read()) if(!string.IsNullOrWhiteSpace(T(r,1))) fields.Add(new(T(r,1),T(r,0),_codeTips.Get("AI_CORRECTION_FIELD",T(r,0)))); }
            if(!methods.Any(x=>x.Key=="S00")) methods.Insert(0,new("S00","S00 · 동일 PDF"));
            using(var c=Open("msds")) using(var tx=c.BeginTransaction(deferred:true))
            {
                using(var q=c.CreateCommand()) { q.Transaction=tx; q.CommandText=@"SELECT ai_correctionh_id,ai_correctionh_corrected_at,ai_correctionh_supplier_name,ai_correctionh_product_name,ai_correctionh_document_type,ai_correctionh_field_key,ai_correctionh_original_value,ai_correctionh_corrected_value,ai_correctionh_learning_status,ai_correctionh_corrected_by,ai_correctionh_pdf_path,ai_correctionh_pdf_hash,ai_correctionh_status_changed_by,ai_correctionh_status_changed_at,ai_correctionh_status_reason,ai_correctionh_source_text,ai_correctionh_source_context,ai_correctionh_source_page FROM ai_correctionh_hist ORDER BY ai_correctionh_corrected_at DESC,ai_correctionh_id DESC"; using var r=q.ExecuteReader(); while(r.Read()) rows.Add(new(T(r,0),T(r,1),T(r,2),T(r,3),T(r,4),T(r,5),T(r,6),T(r,7),T(r,8),T(r,9),T(r,10),T(r,11),T(r,12),T(r,13),T(r,14),T(r,15),T(r,16),T(r,17))); }
                var map=rows.ToDictionary(x=>x.Id);
                using(var q=c.CreateCommand()) { q.Transaction=tx; q.CommandText=@"SELECT ai_applyh_id,ai_applyh_correction_id,CASE WHEN ai_applyh_type='SAME' THEN 'S00' ELSE COALESCE(NULLIF(ai_applyh_subtype,''),'SIMILAR') END,ai_applyh_status FROM ai_applyh_hist WHERE ai_applyh_status IN ('CONFIRMED','RECORRECTED')"; using var r=q.ExecuteReader(); while(r.Read()) if(map.TryGetValue(T(r,1),out var row)) row.Applications.Add(new(T(r,0),T(r,2),T(r,3)=="RECORRECTED")); }
                tx.Commit();
            }
            foreach(var key in rows.SelectMany(x=>x.Applications).Select(x=>x.Method).Distinct()) if(!methods.Any(x=>x.Key==key)) methods.Add(new(key,key));
            _all.Clear(); _all.AddRange(rows); _methods=methods.OrderBy(x=>x.Key).ToList();
            var registered=fields.GroupBy(x=>x.Key,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToList();
            foreach(var row in rows) row.FieldLabel=registered.FirstOrDefault(x=>string.Equals(x.Key,DbField(row.FieldKey),StringComparison.OrdinalIgnoreCase))?.Label ?? row.FieldKey;
            Options(Field,registered.Concat(rows.Select(x=>DbField(x.FieldKey)).Distinct(StringComparer.OrdinalIgnoreCase).Where(k=>!registered.Any(f=>string.Equals(f.Key,k,StringComparison.OrdinalIgnoreCase))).Select(k=>new Option(k,k))));
            Options(State,new[]{new Option("APPROVED","활성"),new Option("INACTIVE","비활성"),new Option("PENDING","대기")}.Concat(_all.Select(x=>x.State).Distinct().Where(s=>s!="APPROVED"&&s!="INACTIVE"&&s!="PENDING").Select(s=>new Option(s,s))));
            Options(Method,new[]{new Option("SAME","대분류 · SAME"),new Option("SIMILAR_ALL","대분류 · SIMILAR")}.Concat(_methods));
            Status("AI 교정학습 조회 완료");
        }
        catch(Exception ex) { _all.Clear(); Results.ItemsSource=null; Error("AI 교정학습 조회 실패",ex); }
        finally { _busy=false; Apply(); }
    }
    private static bool MatchOption(ComboBox c,string s) => c.SelectedValue is not string v || v.Length==0 || v==s;
    private void Apply()
    {
        if(_busy || Results==null) return;
        var id=(Results.SelectedItem as Learning)?.Id;
        var words=_submittedSearch.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        bool or=_submittedLogic==1; var method=Method.SelectedValue as string ?? "";
        var rows=_all.Where(x=>(Field.SelectedValue is not string fk || fk.Length==0 || string.Equals(fk,DbField(x.FieldKey),StringComparison.OrdinalIgnoreCase))&&MatchOption(State,x.State)&& (method==""||x.Applications.Any(a=>method=="SAME"?a.Method=="S00":method=="SIMILAR_ALL"?a.Method!="S00":a.Method==method))).Where(x=>{ var text=string.Join(" ",x.Id,x.Supplier,x.Product,x.Doc,x.FieldLabel,x.FieldKey,x.Before,x.After,x.By,x.StateLabel,x.Evidence,x.Context); return words.Length==0 || (or?words.Any(w=>text.Contains(w,StringComparison.OrdinalIgnoreCase)):words.All(w=>text.Contains(w,StringComparison.OrdinalIgnoreCase))); }).ToList();
        Results.ItemsSource=rows; Results.SelectedItem=rows.FirstOrDefault(x=>x.Id==id);
        var apps=rows.SelectMany(x=>x.Applications).ToList(); int revised=apps.Count(x=>x.Revised);
        CountText.Text=$"교정학습 목록 (총 {rows.Count:N0}건)";
        TotalCount.Text=rows.Count.ToString("N0"); ActiveCount.Text=rows.Count(x=>x.State=="APPROVED").ToString("N0"); PendingCount.Text=rows.Count(x=>x.State=="PENDING").ToString("N0"); InactiveCount.Text=rows.Count(x=>x.State=="INACTIVE").ToString("N0"); UseCount.Text=apps.Count.ToString("N0"); RevisedCount.Text=revised.ToString("N0"); RateCount.Text=apps.Count==0?"-":((double)revised/apps.Count).ToString("P1");
        UpdateSelection();
    }
    private void FilterChanged(object sender,SelectionChangedEventArgs e){ Apply(); }
    private void SearchKeyDown(object sender,System.Windows.Input.KeyEventArgs e) { if(e.Key==System.Windows.Input.Key.Enter) { SubmitSearch(); e.Handled=true; } }
    private void SubmitSearch() { _submittedSearch=Search.Text; _submittedLogic=Logic.SelectedIndex; Apply(); }
    private MsdsLearningDiagnosticWindow? _diagnostic;
    public void CloseDiagnostic() => _diagnostic?.Close();
    private void ResultsDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if(e.OriginalSource is not DependencyObject d) return;
        if(ItemsControl.ContainerFromElement(Results,d) is DataGridRow) OpenDiagnostic();
    }
    private void CasesClick(object sender,RoutedEventArgs e)=>OpenDiagnostic();
    private bool DiagnosticAllowed() => Allowed();
    private void OpenDiagnostic()
    {
        if(!DiagnosticAllowed()) { Status("AI 학습 진단: 접근 제한"); return; }
        if(Results.SelectedItem is not Learning x) return;
        if(_diagnostic==null) { _diagnostic=new MsdsLearningDiagnosticWindow(DiagnosticAllowed,Open,_codeTips); _diagnostic.Closed+=(_,_)=>_diagnostic=null; _diagnostic.Show(); }
        UpdateDiagnostic(x); if(_diagnostic.WindowState==WindowState.Minimized) _diagnostic.WindowState=WindowState.Normal; _diagnostic.Activate();
    }
    private void UpdateDiagnostic(Learning? x) { if(_diagnostic==null) return; if(!DiagnosticAllowed()) { CloseDiagnostic(); return; } _diagnostic.SetLearning(x); }
    private void RowChanged(object sender,SelectionChangedEventArgs e)=>UpdateSelection();
    private void UpdateSelection() { if(Activate==null || Results==null) return; var x=Results.SelectedItem as Learning; Activate.IsEnabled=x!=null&&(x.State=="INACTIVE"||x.State=="PENDING"); Deactivate.IsEnabled=x!=null&&(x.State=="APPROVED"||x.State=="PENDING"); Original.IsEnabled=x!=null; Cases.IsEnabled=x!=null; if(!_busy && _diagnostic!=null) UpdateDiagnostic(x); }
    private void QueryClick(object sender,RoutedEventArgs e)=>SubmitSearch();
    private void RefreshClick(object sender,RoutedEventArgs e)=>Reload();
    private void ResetClick(object sender,RoutedEventArgs e) { _busy=true; Field.SelectedIndex=State.SelectedIndex=Method.SelectedIndex=Logic.SelectedIndex=0; Search.Clear(); _submittedSearch=""; _submittedLogic=0; _busy=false; Apply(); }
    private void ActivateClick(object sender,RoutedEventArgs e)=>Change("APPROVED");
    private void DeactivateClick(object sender,RoutedEventArgs e)=>Change("INACTIVE");
    private void Change(string next)
    {
        if(Results.SelectedItem is not Learning x) return;
        try {
            if(!Allowed()) { Status("학습 상태 변경: 권한 없음"); return; }
            var user=Owner?.GetCurrentUser(); if(user==null) return;
            if(next=="APPROVED" && x.State!="INACTIVE" && x.State!="PENDING") { Status("활성화 가능한 상태가 아닙니다."); return; }
            if (next==x.State || (next=="INACTIVE" && x.State!="APPROVED" && x.State!="PENDING")) { Status("학습 상태 변경 제한"); return; }
            string label=next=="APPROVED"?"활성화":"비활성화";
            var dialog=new Window { Title=label+" 확인", Width=440,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner };
            var panel=new StackPanel{Margin=new Thickness(18)}; dialog.Content=panel;
            panel.Children.Add(new TextBlock{Text=$"선택한 학습데이터를 {label}하시겠습니까?",TextWrapping=System.Windows.TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)});
            var buttons=new StackPanel{Orientation=System.Windows.Controls.Orientation.Horizontal,HorizontalAlignment=System.Windows.HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)}; panel.Children.Add(buttons);
            var ok=new Button{Content="네",Style=(Style)FindResource("LearningButton"),IsDefault=true}; var cancel=new Button{Content="아니오",Style=(Style)FindResource("LearningButton"),IsCancel=true}; buttons.Children.Add(ok); buttons.Children.Add(cancel); ok.Click+=(_,_)=>dialog.DialogResult=true;
            if(QcsModal.Show(dialog,Owner)!=true) { Status(label+" 취소"); return; }
            if(!Allowed()) { Status("학습 상태 변경: 권한 없음"); return; }
            int count; using(var c=Open("msds")) using(var q=c.CreateCommand()) { q.CommandText=@"UPDATE ai_correctionh_hist SET ai_correctionh_learning_status=$next,ai_correctionh_status_changed_by=$user,ai_correctionh_status_changed_at=$time,ai_correctionh_status_reason=$reason WHERE ai_correctionh_id=$id AND ai_correctionh_learning_status=$old"; q.Parameters.AddWithValue("$next",next); q.Parameters.AddWithValue("$user",user.WindowsId); q.Parameters.AddWithValue("$time",DateTimeOffset.Now.ToString("O")); q.Parameters.AddWithValue("$reason",DBNull.Value); q.Parameters.AddWithValue("$id",x.Id); q.Parameters.AddWithValue("$old",x.State); count=q.ExecuteNonQuery(); }
            Reload(); Status(count==1?$"{x.Id} {label} 완료":"상태가 이미 변경되었습니다. 다시 확인하세요.");
        } catch(Exception ex) { Error("학습 상태 변경 실패",ex); }
    }
    private void OriginalClick(object sender,RoutedEventArgs e)
    {
        if(Results.SelectedItem is not Learning x) return;
        try { if(!Allowed()) { Status("원본 PDF 접근 제한"); return; } if(!File.Exists(x.Path)||!string.Equals(Path.GetExtension(x.Path),".pdf",StringComparison.OrdinalIgnoreCase)) { Status("원본 PDF 없음: 저장된 학습 근거를 확인하세요."); return; } using(var stream=File.OpenRead(x.Path)) if(!string.Equals(Convert.ToHexString(SHA256.HashData(stream)),x.Hash,StringComparison.OrdinalIgnoreCase)) { Status("원본 PDF Hash 불일치: 열기 제한"); return; } Process.Start(new ProcessStartInfo(x.Path){UseShellExecute=true}); Status("원본 PDF 열기: "+x.Id); } catch(Exception ex) { Error("원본 PDF 열기 실패",ex); }
    }
}
