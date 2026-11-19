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
    public sealed record Option(string Key, string Label);
    public sealed record Usage(string Id, string Method, bool Revised);
    public sealed record Learning(string Id, string When, string Supplier, string Product, string Doc, string FieldKey, string Before, string After, string State, string By, string Path, string Hash, string ChangedBy, string ChangedAt, string Reason, string Evidence, string Context, string Page)
    {
        public List<Usage> Applications { get; } = new();
        public string FieldLabel => Fields.FirstOrDefault(x => x.Key == FieldKey)?.Label ?? FieldKey;
        public string StateLabel => State switch { "APPROVED" => "활성", "INACTIVE" => "비활성", "PENDING" => "대기", _ => State };
        public string WhenDisplay => DateTimeOffset.TryParse(When, out var date) ? date.ToString("yyyy-MM-dd HH:mm:ss") : When;
        public int Uses => Applications.Count;
        public int Revised => Applications.Count(x => x.Revised);
        public double? Rate => Uses == 0 ? null : (double)Revised / Uses;
    }
    private static readonly Option[] Fields = {
        new("ProductNameBox","제품명"), new("SupplierNameBox","공급업체"), new("GracoCodeBox","GRACO 코드"), new("RevisionDateBox","개정일"), new("RevisionNumberBox","개정번호"), new("PhoneBox","전화번호"), new("AddressBox","주소"), new("UnNumberBox","UN 번호"), new("HazardCodesBox","유해위험 문구"), new("AutoIgnitionBox","자연발화온도"), new("VaporPressureBox","증기압"), new("LelBox","폭발하한"), new("UelBox","폭발상한"), new("FlashPointBox","인화점"), new("CorrosivityBox","부식성"), new("PersonalProtectionBox","보호구"), new("MoelSubmissionBox","제출번호") };
    private readonly List<Learning> _all = new();
    private List<Option> _methods = new();
    private bool _busy = true;
    private MainWindow? Owner => Window.GetWindow(this) as MainWindow;
    public MsdsLearningManagementView() { InitializeComponent(); Loaded += (_,_) => Reload(); }
    private static SqliteConnection Open(string name) { var c = new SqliteConnection(DatabaseManager.GetConnectionString(name)); try { c.Open(); return c; } catch { c.Dispose(); throw; } }
    private static string T(SqliteDataReader r,int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i)) ?? "";
    private bool Allowed() => QcsAuthorizationService.CanAccessProgram(Owner?.GetCurrentUser(), ProgramId);
    private void Status(string s) => Owner?.SetRunStatus(s);
    private void Error(string text, Exception ex) { Status(text + ": " + ex.Message); MessageBox.Show(text + "\n" + ex.Message,"오류",MessageBoxButton.OK,MessageBoxImage.Error); }
    private static void Choices(ComboBox c, IEnumerable<string> values) { var old = c.SelectedItem as string; var list = new[]{"전체"}.Concat(values.Where(x=>x.Length>0).Distinct().OrderBy(x=>x)).ToList(); c.ItemsSource=list; c.SelectedItem=list.Contains(old ?? "") ? old : "전체"; }
    private static void Options(ComboBox c, IEnumerable<Option> values) { var old=c.SelectedValue as string; var list=new[]{new Option("","전체")}.Concat(values).ToList(); c.ItemsSource=list; c.SelectedValue=list.Any(x=>x.Key==old) ? old : ""; }
    private void Reload()
    {
        _busy=true;
        try
        {
            if (!Allowed()) { _all.Clear(); Results.ItemsSource=null; Status("AI 교정학습 관리: 접근 제한"); return; }
            var rows=new List<Learning>(); var methods=new List<Option>();
            using(var c=Open("common")) using(var q=c.CreateCommand()) { q.CommandText="SELECT zx_code_value,zx_code_desc1 FROM zx_code_mstr WHERE zx_code_fldname='AI_SIMILAR_METHOD' ORDER BY zx_code_value"; using var r=q.ExecuteReader(); while(r.Read()) methods.Add(new(T(r,0),T(r,0)+" · "+T(r,1))); }
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
            Choices(Supplier,_all.Select(x=>x.Supplier));
            Options(Field,Fields.Concat(_all.Select(x=>x.FieldKey).Distinct().Where(k=>!Fields.Any(f=>f.Key==k)).Select(k=>new Option(k,k))));
            Options(State,new[]{new Option("APPROVED","활성"),new Option("INACTIVE","비활성"),new Option("PENDING","대기")}.Concat(_all.Select(x=>x.State).Distinct().Where(s=>s!="APPROVED"&&s!="INACTIVE"&&s!="PENDING").Select(s=>new Option(s,s))));
            Options(Method,new[]{new Option("SAME","대분류 · SAME"),new Option("SIMILAR_ALL","대분류 · SIMILAR")}.Concat(_methods));
            Status("AI 교정학습 조회 완료");
        }
        catch(Exception ex) { _all.Clear(); Results.ItemsSource=null; Error("AI 교정학습 조회 실패",ex); }
        finally { _busy=false; Apply(); }
    }
    private static bool Match(ComboBox c,string s) => c.SelectedItem is not string v || v=="전체" || v==s;
    private static bool MatchOption(ComboBox c,string s) => c.SelectedValue is not string v || v.Length==0 || v==s;
    private void Apply()
    {
        if(_busy || Results==null) return;
        var id=(Results.SelectedItem as Learning)?.Id;
        var words=Search.Text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        bool or=Logic.SelectedIndex==1; var method=Method.SelectedValue as string ?? "";
        var rows=_all.Where(x=>Match(Supplier,x.Supplier)&&MatchOption(Field,x.FieldKey)&&MatchOption(State,x.State)&& (method==""||x.Applications.Any(a=>method=="SAME"?a.Method=="S00":method=="SIMILAR_ALL"?a.Method!="S00":a.Method==method))).Where(x=>{ var text=string.Join(" ",x.Id,x.Supplier,x.Product,x.Doc,x.FieldLabel,x.FieldKey,x.Before,x.After,x.By,x.StateLabel,x.Evidence,x.Context); return words.Length==0 || (or?words.Any(w=>text.Contains(w,StringComparison.OrdinalIgnoreCase)):words.All(w=>text.Contains(w,StringComparison.OrdinalIgnoreCase))); }).ToList();
        Results.ItemsSource=rows; Results.SelectedItem=rows.FirstOrDefault(x=>x.Id==id);
        var apps=rows.SelectMany(x=>x.Applications).ToList(); int revised=apps.Count(x=>x.Revised);
        CountText.Text=$"교정학습 목록 (총 {rows.Count:N0}건)";
        TotalCount.Text=rows.Count.ToString("N0"); ActiveCount.Text=rows.Count(x=>x.State=="APPROVED").ToString("N0"); PendingCount.Text=rows.Count(x=>x.State=="PENDING").ToString("N0"); InactiveCount.Text=rows.Count(x=>x.State=="INACTIVE").ToString("N0"); UseCount.Text=apps.Count.ToString("N0"); RevisedCount.Text=revised.ToString("N0"); RateCount.Text=apps.Count==0?"-":((double)revised/apps.Count).ToString("P1");
        UpdateSelection();
    }
    private void FilterChanged(object sender,SelectionChangedEventArgs e)=>Apply();
    private void SearchChanged(object sender,TextChangedEventArgs e)=>Apply();
    private void RowChanged(object sender,SelectionChangedEventArgs e)=>UpdateSelection();
    private void UpdateSelection() { if(Activate==null || Results==null) return; var x=Results.SelectedItem as Learning; Activate.IsEnabled=x!=null&&x.State!="APPROVED"; Deactivate.IsEnabled=x!=null&&(x.State=="APPROVED"||x.State=="PENDING"); Original.IsEnabled=x!=null; }
    private void QueryClick(object sender,RoutedEventArgs e)=>Apply();
    private void RefreshClick(object sender,RoutedEventArgs e)=>Reload();
    private void ResetClick(object sender,RoutedEventArgs e) { _busy=true; Supplier.SelectedIndex=Field.SelectedIndex=State.SelectedIndex=Method.SelectedIndex=Logic.SelectedIndex=0; Search.Clear(); _busy=false; Apply(); }
    private void ActivateClick(object sender,RoutedEventArgs e)=>Change("APPROVED");
    private void DeactivateClick(object sender,RoutedEventArgs e)=>Change("INACTIVE");
    private void Change(string next)
    {
        if(Results.SelectedItem is not Learning x) return;
        try {
            if(!Allowed()) { Status("학습 상태 변경: 권한 없음"); return; }
            var user=Owner?.GetCurrentUser(); if(user==null) return;
            if(next=="APPROVED" && (x.FieldKey=="GracoCodeBox" || string.IsNullOrWhiteSpace(x.Hash) || string.IsNullOrWhiteSpace(x.Evidence) || !int.TryParse(x.Page,out var page) || page<=0)) { Status("활성화 제한: 학습 근거 부족 또는 GRACO 항목"); return; }
            if (next==x.State || (next=="INACTIVE" && x.State!="APPROVED" && x.State!="PENDING")) { Status("학습 상태 변경 제한"); return; }
            string label=next=="APPROVED"?"활성화":"비활성화";
            var dialog=new Window { Title=label+" 확인", Width=440,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner };
            var panel=new StackPanel{Margin=new Thickness(18)}; dialog.Content=panel;
            panel.Children.Add(new TextBlock{Text=$"{x.Id} 학습을 {label}하시겠습니까?\n변경 사유를 입력하세요.",TextWrapping=System.Windows.TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)});
            var reason=new TextBox{Style=(Style)FindResource("QcsInput")}; panel.Children.Add(reason);
            var buttons=new StackPanel{Orientation=System.Windows.Controls.Orientation.Horizontal,HorizontalAlignment=System.Windows.HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)}; panel.Children.Add(buttons);
            var ok=new Button{Content="확인",Style=(Style)FindResource("LearningButton"),IsEnabled=false}; var cancel=new Button{Content="취소",Style=(Style)FindResource("LearningButton"),IsCancel=true}; buttons.Children.Add(ok); buttons.Children.Add(cancel); reason.TextChanged+=(_,_)=>ok.IsEnabled=!string.IsNullOrWhiteSpace(reason.Text); ok.Click+=(_,_)=>dialog.DialogResult=true;
            if(QcsModal.Show(dialog,Owner)!=true) { Status(label+" 취소"); return; }
            if(!Allowed()) { Status("학습 상태 변경: 권한 없음"); return; }
            int count; using(var c=Open("msds")) using(var q=c.CreateCommand()) { q.CommandText=@"UPDATE ai_correctionh_hist SET ai_correctionh_learning_status=$next,ai_correctionh_status_changed_by=$user,ai_correctionh_status_changed_at=$time,ai_correctionh_status_reason=$reason WHERE ai_correctionh_id=$id AND ai_correctionh_learning_status=$old"; q.Parameters.AddWithValue("$next",next); q.Parameters.AddWithValue("$user",user.WindowsId); q.Parameters.AddWithValue("$time",DateTimeOffset.Now.ToString("O")); q.Parameters.AddWithValue("$reason",reason.Text.Trim()); q.Parameters.AddWithValue("$id",x.Id); q.Parameters.AddWithValue("$old",x.State); count=q.ExecuteNonQuery(); }
            Reload(); Status(count==1?$"{x.Id} {label} 완료":"상태가 이미 변경되었습니다. 다시 확인하세요.");
        } catch(Exception ex) { Error("학습 상태 변경 실패",ex); }
    }
    private void OriginalClick(object sender,RoutedEventArgs e)
    {
        if(Results.SelectedItem is not Learning x) return;
        try { if(!Allowed()) { Status("원본 PDF 접근 제한"); return; } if(!File.Exists(x.Path)||!string.Equals(Path.GetExtension(x.Path),".pdf",StringComparison.OrdinalIgnoreCase)) { Status("원본 PDF 없음: 저장된 학습 근거를 확인하세요."); return; } using(var stream=File.OpenRead(x.Path)) if(!string.Equals(Convert.ToHexString(SHA256.HashData(stream)),x.Hash,StringComparison.OrdinalIgnoreCase)) { Status("원본 PDF Hash 불일치: 열기 제한"); return; } Process.Start(new ProcessStartInfo(x.Path){UseShellExecute=true}); Status("원본 PDF 열기: "+x.Id); } catch(Exception ex) { Error("원본 PDF 열기 실패",ex); }
    }
}
