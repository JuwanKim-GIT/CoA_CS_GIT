using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Wpf;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using Learning = QCS.MsdsLearningManagementView.Learning;

namespace QCS;
public partial class MsdsLearningDiagnosticWindow : Window
{
    public sealed record Case(string LearningId,string Id,string Date,string Product,string Method,string Before,string After,string Final,string Result,string NewId,string Path,string Hash,string Page,string Evidence,string Context,string Label,string? MethodTooltip);
    private readonly Func<bool> _allowed;
    private readonly QcsCodeTooltips _codeTips;
    private readonly Func<string,SqliteConnection> _open;
    private readonly List<string> _temporary=new();
    private bool _closed;
    private int _leftVersion,_rightVersion;
    private Learning? _current;
    public MsdsLearningDiagnosticWindow(Func<bool> allowed,Func<string,SqliteConnection> open,QcsCodeTooltips codeTips)
    {
        _allowed=allowed; _open=open; _codeTips=codeTips; InitializeComponent();
        // Use the monitor containing the window for initial sizing, in WPF DIPs.
        SourceInitialized+=(_,_)=> {
            var handle=new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var screen=System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(this);
            Width=Math.Min(Width,screen.Width/dpi.DpiScaleX);
            Height=Math.Min(Height,screen.Height/dpi.DpiScaleY);
        };
        Closed+=(_,_)=>{_closed=true; LeftPdf.Dispose(); RightPdf.Dispose(); foreach(var p in _temporary) try{File.Delete(p);}catch(IOException){}catch(UnauthorizedAccessException){} };
    }
    private static string T(SqliteDataReader r,int i)=>r.IsDBNull(i)?"":Convert.ToString(r.GetValue(i))??"";
    private static string DateText(string s)=>DateTimeOffset.TryParse(s,out var d)?d.ToString("yyyy-MM-dd HH:mm:ss"):s;
    public void SetLearning(Learning? x)
    {
        if(_closed || !_allowed()) return;
        if(ReferenceEquals(x,_current)) return; _current=x;
        History.ItemsSource=null;
        if(x==null){_=Preview(true,"","","","",""); _=Preview(false,"","","","",""); return;}
        _=Preview(true,x.Path,x.Hash,x.Page,x.Evidence,x.Context);
        try{
            var rows=new List<Case>(); using var c=_open("msds"); using var q=c.CreateCommand();
            q.CommandText=@"SELECT ai_applyh_id,ai_applyh_finalized_at,ai_applyh_product_name,CASE WHEN ai_applyh_type='SAME' THEN 'S00' ELSE COALESCE(NULLIF(ai_applyh_subtype,''),'SIMILAR') END,ai_applyh_value_before,ai_applyh_value_after,ai_applyh_value_final,ai_applyh_status,ai_applyh_new_correction_id,ai_applyh_pdf_path,ai_applyh_pdf_hash,ai_applyh_source_page,ai_applyh_source_text,ai_applyh_source_context,ai_applyh_source_label FROM ai_applyh_hist WHERE ai_applyh_correction_id=$id AND ai_applyh_status IN ('CONFIRMED','RECORRECTED') ORDER BY ai_applyh_finalized_at DESC,ai_applyh_id DESC";
            q.Parameters.AddWithValue("$id",x.Id); using var r=q.ExecuteReader(); while(r.Read()) rows.Add(new(x.Id,T(r,0),DateText(T(r,1)),T(r,2),T(r,3),T(r,4),T(r,5),T(r,6),T(r,7)=="RECORRECTED"?"재교정":"확정",T(r,8),T(r,9),T(r,10),T(r,11),T(r,12),T(r,13),T(r,14),_codeTips.Get("AI_SIMILAR_METHOD",T(r,3)) ?? T(r,3)));
            History.ItemsSource=rows; if(rows.Count>0) History.SelectedIndex=0; else _=Preview(false,"","","","","적용사례 없음");
        }catch(Exception ex){_=Preview(false,"","","","","적용이력 조회 실패: "+ex.Message);}
    }
    private void HistoryChanged(object sender,SelectionChangedEventArgs e)
    {
        if(History.SelectedItem is Case a) _=Preview(false,a.Path,a.Hash,a.Page,a.Evidence,a.Label+"\n"+a.Context); else _=Preview(false,"","","","","");
    }
    private async Task Preview(bool left,string path,string hash,string page,string evidence,string context)
    {
        int version=left?++_leftVersion:++_rightVersion; var view=left?LeftPdf:RightPdf; var status=left?LeftStatus:RightStatus; var text=left?LeftEvidence:RightEvidence;
        bool Current()=>!_closed && version==(left?_leftVersion:_rightVersion);
        var fallback=left?LeftFallback:RightFallback; fallback.Visibility=Visibility.Visible;
        text.Text=string.Join("\n",evidence,context).Trim(); status.Text="PDF 확인 중"; view.Visibility=Visibility.Hidden;
        try{
            if(!_allowed() || !File.Exists(path) || !string.Equals(Path.GetExtension(path),".pdf",StringComparison.OrdinalIgnoreCase)){status.Text=path.Length==0?"PDF 없음 / 선택 대기":"PDF 파일 없음 또는 접근 제한 — 저장된 근거 표시";return;}
            int n=int.TryParse(page,out var p)&&p>0?p:1; string temp=Path.Combine(Path.GetTempPath(),"QCS-learning-"+Guid.NewGuid().ToString("N")+".pdf"); _temporary.Add(temp);
            var result=await Task.Run(()=>{using(var f=File.OpenRead(path)) if(!string.Equals(Convert.ToHexString(SHA256.HashData(f)),hash,StringComparison.OrdinalIgnoreCase)) return (Valid:false,Marked:false); bool marked=false; if(int.TryParse(page,out var pn)&&pn>0&&!string.IsNullOrWhiteSpace(evidence)) try{marked=MsdsPdfHighlightPreview.TryCreate(path,temp,pn,evidence);}catch{marked=false;} if(!marked) File.Copy(path,temp,true); return (Valid:true,Marked:marked);});
            if(!Current()){try{File.Delete(temp);}catch{} return;}
            if(!result.Valid){status.Text="PDF Hash 불일치 — 저장된 근거 표시";return;}
            await view.EnsureCoreWebView2Async(); if(!Current())return;
            if(!_allowed()){status.Text="PDF 접근 제한";return;}
            view.CoreWebView2.Navigate(new Uri(temp).AbsoluteUri+"#page="+n); view.Visibility=Visibility.Visible; fallback.Visibility=Visibility.Collapsed;
            status.Text=(string.IsNullOrWhiteSpace(page)?"원문 페이지 미확인":page+" 페이지")+(result.Marked?" · 원문 강조 표시":" · 원문 위치 미확정");
        }catch(Exception ex){if(Current()){view.Visibility=Visibility.Hidden;fallback.Visibility=Visibility.Visible;status.Text="PDF 표시 실패: "+ex.Message;}}
    }
}
