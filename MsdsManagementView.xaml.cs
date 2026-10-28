using System;
using System.Data;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;

// WPF / Windows Forms 이름 충돌 방지
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using MessageBox = System.Windows.MessageBox;

namespace QCS
{
 public partial class MsdsManagementView : UserControl
 {
  private DataTable _master = new DataTable();
  private string _selectedPdfPath = "";
  private bool _previewExpanded = true;
  private bool _filesExpanded = true;
  private bool _zoomInitialized;
  private int _previewRequest;
  public MsdsManagementView() { InitializeComponent(); Loaded += (_, __) => RefreshData(); }
  private static string Value(DataRowView row, string key) => row.Row.Table.Columns.Contains(key) ? Convert.ToString(row[key]) ?? "" : "";
  private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshData();
  private void Search_Click(object sender, RoutedEventArgs e) => ApplyFilter();
  private void Reset_Click(object sender, RoutedEventArgs e) { SearchCode.Text=""; SearchProduct.Text=""; SearchSupplier.Text=""; LatestOnly.IsChecked=false; ApplyFilter(); }
  private void ApplyFilter()
  {
   if (_master.Columns.Count == 0) return;
   var filters = new System.Collections.Generic.List<string>();
   static string Escape(string value) => value.Replace("'", "''").Replace("[", "[[]").Replace("%", "[%]").Replace("*", "[*]");
   if (!string.IsNullOrWhiteSpace(SearchCode.Text)) filters.Add($"Convert(msds_graco_code, 'System.String') LIKE '%{Escape(SearchCode.Text.Trim())}%'");
   if (!string.IsNullOrWhiteSpace(SearchProduct.Text)) filters.Add($"Convert(msds_product_name, 'System.String') LIKE '%{Escape(SearchProduct.Text.Trim())}%'");
   if (!string.IsNullOrWhiteSpace(SearchSupplier.Text)) filters.Add($"Convert(msds_supplier_name, 'System.String') LIKE '%{Escape(SearchSupplier.Text.Trim())}%'");
   if (LatestOnly.IsChecked == true) filters.Add("Convert(msds_is_latest, 'System.String') = '1'");
   _master.DefaultView.RowFilter = string.Join(" AND ", filters);
   MasterCount.Text = $"MSDS 목록 ({_master.DefaultView.Count}건)";
   MasterGrid.SelectedIndex = _master.DefaultView.Count > 0 ? 0 : -1;
   if (_master.DefaultView.Count == 0) ClearDetails();
  }
  private static DataTable ReadTable(SqliteConnection connection, string sql, long? id = null)
  {
   using var cmd = connection.CreateCommand(); cmd.CommandText=sql;
   if (id.HasValue) cmd.Parameters.AddWithValue("$id", id.Value);
   using var reader=cmd.ExecuteReader(); var result=new DataTable();
   for(int i=0;i<reader.FieldCount;i++) result.Columns.Add(reader.GetName(i), typeof(string));
   while(reader.Read()) { var row=result.NewRow(); for(int i=0;i<reader.FieldCount;i++) row[i]=reader.IsDBNull(i)?"":Convert.ToString(reader.GetValue(i))??""; result.Rows.Add(row); }
   return result;
  }
  private static SqliteConnection OpenReadOnly()
  {
   string dbPath=DatabaseManager.ActiveDbPath;
   if(string.IsNullOrWhiteSpace(dbPath)||!File.Exists(dbPath)) throw new FileNotFoundException("QCS DB 파일이 존재하지 않습니다. DB 연결 상태를 확인하세요.",dbPath);
   var builder=new SqliteConnectionStringBuilder { DataSource=dbPath,Mode=SqliteOpenMode.ReadOnly,DefaultTimeout=10,Pooling=false };
   var connection=new SqliteConnection(builder.ToString()); connection.Open(); return connection;
  }
  private void RefreshData()
  {
   try
   {
    using var connection=OpenReadOnly();
    _master=ReadTable(connection,"SELECT * FROM msds_mstr ORDER BY msds_msds_id DESC");
    MasterGrid.ItemsSource=_master.DefaultView;
    DetailGrid.ItemsSource=null;
    ClearDetails(); ApplyFilter();
    StatusText.Text=$"DB: {DatabaseManager.ActiveDbPath} | 마지막 조회: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
   }
   catch(Exception ex) { StatusText.Text="DB 조회 실패: "+ex.Message; MessageBox.Show(ex.Message,"MSDS DB 조회 오류",MessageBoxButton.OK,MessageBoxImage.Error); }
  }
  private void ClearDetails()
  {
   foreach(var child in BasicFields.Children) if(child is TextBox box) box.Text="";
   DetailGrid.ItemsSource=null; DetailCount.Text="구성성분 (0건)";
   _selectedPdfPath=""; PdfFileName.Text="선택된 PDF 없음";
   _previewRequest++;
   if(PdfBrowser.CoreWebView2 != null) PdfBrowser.CoreWebView2.Navigate("about:blank");
   PreviewFallback.Visibility=Visibility.Visible;
   PreviewMessage.Text="MSDS를 선택하세요.";
  }
  private void MasterGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
  {
   if(MasterGrid.SelectedItem is not DataRowView row || !long.TryParse(Value(row,"msds_msds_id"),out long id)) { ClearDetails(); return; }
   foreach(var child in BasicFields.Children)
    if(child is TextBox box && box.Tag is string key) box.Text=key=="msds_is_latest" ? (Value(row,key)=="1"?"예":"아니오") : Value(row,key);
   _selectedPdfPath=Value(row,"msds_file_path");
   PdfFileName.Text=Value(row,"msds_file_name");
   LoadPreview();
   try
   {
    using var connection=OpenReadOnly();
    var detail=ReadTable(connection,"SELECT * FROM msds_comp_dtl WHERE msds_comp_parent_msds_id = $id ORDER BY msds_comp_sort_order, msds_comp_msds_id",id);
    DetailGrid.ItemsSource=detail.DefaultView; DetailCount.Text=$"구성성분 ({detail.Rows.Count}건) — MSDS ID: {id}";
   }
   catch(Exception ex) { DetailGrid.ItemsSource=null; DetailCount.Text="구성성분 조회 실패"; StatusText.Text="구성성분 조회 실패: "+ex.Message; }
  }
  private async void LoadPreview()
  {
   int request = ++_previewRequest;
   PreviewFallback.Visibility=Visibility.Visible;
   if(string.IsNullOrWhiteSpace(_selectedPdfPath)||!File.Exists(_selectedPdfPath))
   { PreviewMessage.Text="PDF 파일을 찾을 수 없습니다."; if(PdfBrowser.CoreWebView2 != null) PdfBrowser.CoreWebView2.Navigate("about:blank"); return; }
   PreviewMessage.Text="PDF 원문을 불러오는 중입니다.";
   if(!_previewExpanded) return;
   try
   {
    await PdfBrowser.EnsureCoreWebView2Async();
    if(request != _previewRequest || !_previewExpanded) return;
    if(!_zoomInitialized)
    {
     _zoomInitialized=true;
     PdfBrowser.ZoomFactorChanged += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateZoomText));
    }
    PdfBrowser.CoreWebView2.Navigate(new Uri(Path.GetFullPath(_selectedPdfPath)).AbsoluteUri);
    PreviewFallback.Visibility=Visibility.Collapsed;
    UpdateZoomText();
   }
   catch(Exception ex) { PreviewMessage.Text="PDF 미리보기 오류: " + ex.Message; PreviewFallback.Visibility=Visibility.Visible; }
  }
  private void UpdateZoomText() => ZoomText.Text=$"{PdfBrowser.ZoomFactor*100:0}%";
  private void ZoomIn_Click(object sender,RoutedEventArgs e) { PdfBrowser.ZoomFactor=Math.Clamp(PdfBrowser.ZoomFactor*1.1,0.25,5); UpdateZoomText(); }
  private void ZoomOut_Click(object sender,RoutedEventArgs e) { PdfBrowser.ZoomFactor=Math.Clamp(PdfBrowser.ZoomFactor/1.1,0.25,5); UpdateZoomText(); }
  private void TogglePreview_Click(object sender,RoutedEventArgs e)
  {
   _previewExpanded=!_previewExpanded;
   PreviewPanel.Visibility=_previewExpanded?Visibility.Visible:Visibility.Collapsed;
   RightSplitter.Visibility=_previewExpanded?Visibility.Visible:Visibility.Collapsed;
   WorkspaceGrid.ColumnDefinitions[4].Width=_previewExpanded?new GridLength(390):new GridLength(0);
   WorkspaceGrid.ColumnDefinitions[3].Width=_previewExpanded?new GridLength(6):new GridLength(0);
   TogglePreviewButton.Content=_previewExpanded?"▣  원문 접기":"▣  원문 보기";
   if(_previewExpanded) LoadPreview();
  }
  private void ToggleFiles_Click(object sender,RoutedEventArgs e)
  {
   _filesExpanded=!_filesExpanded;
   FilesPanel.Visibility=_filesExpanded?Visibility.Visible:Visibility.Collapsed;
   LeftSplitter.Visibility=_filesExpanded?Visibility.Visible:Visibility.Collapsed;
   WorkspaceGrid.ColumnDefinitions[0].Width=_filesExpanded?new GridLength(310):new GridLength(0);
   WorkspaceGrid.ColumnDefinitions[1].Width=_filesExpanded?new GridLength(6):new GridLength(0);
   ToggleFilesButton.Content=_filesExpanded?"☰  목록 접기":"☰  MSDS 목록";
  }
  private void OpenPdf_Click(object sender,RoutedEventArgs e)
  {
   if(string.IsNullOrWhiteSpace(_selectedPdfPath)||!File.Exists(_selectedPdfPath)) { MessageBox.Show("원본 PDF 파일을 찾을 수 없습니다.","PDF 열기",MessageBoxButton.OK,MessageBoxImage.Warning); return; }
   try { Process.Start(new ProcessStartInfo { FileName=_selectedPdfPath,UseShellExecute=true }); }
   catch(Exception ex) { MessageBox.Show(ex.Message,"PDF 열기 실패",MessageBoxButton.OK,MessageBoxImage.Error); }
  }
        private void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            if (MasterGrid.SelectedItem is not DataRowView row ||
                !long.TryParse(Convert.ToString(row["msds_msds_id"]), out long id))
            {
                System.Windows.MessageBox.Show("삭제할 MSDS를 먼저 선택하세요.", "선택 필요");
                return;
            }

            string name = Convert.ToString(row["msds_file_name"]) ?? "";
            string pdfPath = row.Row.Table.Columns.Contains("msds_file_path")
                ? Convert.ToString(row["msds_file_path"]) ?? "" : "";
            var confirm = System.Windows.MessageBox.Show(
                $"선택한 MSDS를 DB에서 영구 삭제하시겠습니까?\n\nID: {id}\n파일: {name}\n\n" +
                "관련 구성성분·규제·추출 결과 및 등록 이력도 삭제됩니다.\n" +
                "보관된 PDF도 삭제를 시도합니다. PDF 삭제 실패 시 DB 레코드는 삭제됩니다.",
                "MSDS DB 레코드 삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                string dbPath = DatabaseManager.ActiveDbPath;
                if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
                    throw new FileNotFoundException("DB 파일을 찾을 수 없습니다.", dbPath);
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadWrite,
                    DefaultTimeout = 10,
                    Pooling = false
                };
                using var connection = new SqliteConnection(builder.ToString());
                connection.Open();
                using (var pragma = connection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
                    pragma.ExecuteNonQuery();
                }
                using var transaction = connection.BeginTransaction();
                // Child records must be deleted first because the schema uses ON DELETE RESTRICT.
                string[] statements =
                {
                    "DELETE FROM reg_result_dtl WHERE reg_result_msds_id=$id",
                    "DELETE FROM extract_result_dtl WHERE extract_result_msds_id=$id",
                    "DELETE FROM msds_comp_dtl WHERE msds_comp_parent_msds_id=$id",
                    "DELETE FROM msds_oper_dtl WHERE msds_oper_parent_msds_id=$id",
                    "DELETE FROM audit_log_hist WHERE audit_log_msds_id=$id",
                    "DELETE FROM msds_mstr WHERE msds_msds_id=$id"
                };
                int deleted = 0;
                foreach (string sql in statements)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("$id", id);
                    int count = cmd.ExecuteNonQuery();
                    if (sql.StartsWith("DELETE FROM msds_mstr", StringComparison.Ordinal))
                        deleted = count;
                }
                if (deleted != 1)
                    throw new InvalidOperationException("삭제 대상 MSDS가 존재하지 않거나 이미 삭제되었습니다.");
                transaction.Commit();
                string pdfWarning = "";
                if (!string.IsNullOrWhiteSpace(pdfPath))
                {
                    try { if (File.Exists(pdfPath)) File.Delete(pdfPath); }
                    catch (Exception pdfEx) { pdfWarning = " PDF 삭제 실패(수동 삭제 필요): " + pdfEx.Message; }
                }
                RefreshData();
                StatusText.Text = $"MSDS ID {id} DB 레코드 삭제 완료." + pdfWarning;
                if (pdfWarning.Length > 0)
                    System.Windows.MessageBox.Show(pdfWarning, "PDF 삭제 안내",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                StatusText.Text = "DB 레코드 삭제 실패: " + ex.Message;
                System.Windows.MessageBox.Show(ex.Message, "MSDS DB 삭제 오류",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


 }
}
