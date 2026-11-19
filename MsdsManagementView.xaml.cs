using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;

// WPF / Windows Forms 이름 충돌 방지
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using MessageBox = System.Windows.MessageBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using VisualTreeHelper = System.Windows.Media.VisualTreeHelper;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace QCS
{
 public partial class MsdsManagementView : UserControl
 {
  private static bool IsLatest(DataRowView row)
  {
   var value = Value(row, "msds_is_latest");
   return value == "1" || value.Equals("Y", StringComparison.OrdinalIgnoreCase);
  }
  private DataTable _master = new DataTable();
  private string _selectedPdfPath = "";
  private bool _previewExpanded = true;
  private bool _filesExpanded = true;

   private long? _detailId;
   private readonly Dictionary<long, string> _componentSearch = new();
  private int _previewRequest;
   private bool _editing;
   private bool _restoringSelection;
   private DataTable? _detailTable;
   private long? _editingId;
   private DataRowView? _lockedMasterRow;
  public MsdsManagementView()
  {
   InitializeComponent();
   Loaded += (_, __) => { UpdateAdaptiveScale(); RefreshData(); };
   SizeChanged += (_, __) => UpdateAdaptiveScale();
  }
  private void MaterialFilter_Checked(object sender, RoutedEventArgs e)
  {
   if (!IsLoaded || _editing || _master.Columns.Count == 0 || SearchText == null) return;
   try { ApplyFilter(); }
   catch (FormatException ex) { PublishStatus("검색 조건 오류: " + ex.Message); }
  }
  private IEnumerable<TextBox> BasicTextBoxes() => DescendantTextBoxes(BasicFields);
  private static IEnumerable<TextBox> DescendantTextBoxes(System.Windows.DependencyObject root)
  {
   for (int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)
   {
    var child=System.Windows.Media.VisualTreeHelper.GetChild(root,i);
    if (child is TextBox box) yield return box;
    else foreach(var nested in DescendantTextBoxes(child)) yield return nested;
   }
  }
  private double _currentUiScale = 1.0;
  private void UpdateAdaptiveScale()
  {
   if (ManagementRoot == null || ActualWidth <= 0 || ActualHeight <= 0) return;
   double scale = Math.Min(1.0, Math.Min(ActualWidth / 1550.0, ActualHeight / 850.0));
   scale = Math.Max(0.70, scale);
   if (Math.Abs(scale - _currentUiScale) < 0.015) return;
   _currentUiScale = scale;
   ManagementRoot.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
  }
  private void DateField_SelectionChanged(object sender, SelectionChangedEventArgs e)
  {
   if (DateFrom == null || DateTo == null || DateField == null) return;
   if (DateField.SelectedIndex == 0) { DateFrom.SelectedDate = null; DateTo.SelectedDate = null; }
   else { DateFrom.SelectedDate = DateTime.Today; DateTo.SelectedDate = DateTime.Today; }
  }
  private string? ResolveDateColumn()
  {
   if (DateField.SelectedIndex <= 0) return null;
   string[] candidates = DateField.SelectedIndex switch
   {
    1 => new[] { "msds_revision_date" },
    2 => new[] { "msds_created_at", "msds_registered_at", "msds_upload_date", "msds_uploaded_at", "msds_created_date", "msds_reg_date" },
    3 => new[] { "msds_updated_at", "msds_modified_at", "msds_modified_date", "msds_update_date" },
    _ => Array.Empty<string>()
   };
   string? column = candidates.FirstOrDefault(_master.Columns.Contains);
   if (column == null) throw new InvalidOperationException($"{((ComboBoxItem)DateField.SelectedItem).Content} 컬럼을 현재 MSDS DB에서 확인할 수 없습니다. DB 스키마 확인이 필요합니다.");
   return column;
  }
  private static bool DateMatches(string raw, DateTime? from, DateTime? to)
  {
   if (string.IsNullOrWhiteSpace(raw)) return false;
   if (!DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date) &&
       !DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return false;
   return (!from.HasValue || date.Date >= from.Value.Date) && (!to.HasValue || date.Date <= to.Value.Date);
  }
  private static string Value(DataRowView row, string key) => row.Row.Table.Columns.Contains(key) ? Convert.ToString(row[key]) ?? "" : "";
   private void PublishStatus(string message)
   {
    if (Window.GetWindow(this) is MainWindow main) main.SetRunStatus(message);
   }
   private void Search_Click(object sender, RoutedEventArgs e) { if (_editing) return; if (ConfirmDiscard()) RefreshData(); }
   private void Search_KeyDown(object sender, KeyEventArgs e)
   {
    if (e.Key == Key.Enter) { if (!_editing && ConfirmDiscard()) RefreshData(); e.Handled = true; }
   }
   // Same expression semantics as UserManagementView: implicit AND, &, |, AND, OR, parentheses.
   private sealed class SearchParser
   {
    private readonly List<string> _tokens;
    private readonly Func<string, bool> _matches;
    private int _pos;
    public SearchParser(string query, Func<string, bool> matches)
    {
     _tokens = Regex.Matches(query, @"\(|\)|\&|\||[^\s()&|]+").Select(m => m.Value).ToList();
     _matches = matches;
    }
    private string? Peek() => _pos < _tokens.Count ? _tokens[_pos] : null;
    private bool Is(string value) => string.Equals(Peek(), value, StringComparison.OrdinalIgnoreCase);
    private bool Take(string value) { if (!Is(value)) return false; _pos++; return true; }
    public bool Evaluate()
    {
     if (_tokens.Count == 0) return true;
     bool result = ParseOr();
     if (Peek() != null) throw new FormatException("검색 연산자 또는 괄호를 확인해 주세요.");
     return result;
    }
    private bool ParseOr()
    {
     bool result = ParseAnd();
     while (Take("OR") || Take("|")) { bool right = ParseAnd(); result = result || right; }
     return result;
    }
    private bool ParseAnd()
    {
     bool result = ParseAtom();
     while (Peek() != null && !Is(")") && !Is("OR") && !Is("|"))
     { Take("AND"); Take("&"); bool right = ParseAtom(); result = result && right; }
     return result;
    }
    private bool ParseAtom()
    {
     if (Take("(")) { bool result = ParseOr(); if (!Take(")")) throw new FormatException("닫는 괄호 ')'가 없습니다."); return result; }
     var term = Peek();
     if (term == null || Is(")") || Is("AND") || Is("OR") || Is("&") || Is("|")) throw new FormatException("검색어 또는 괄호가 필요합니다.");
     _pos++; return _matches(term);
    }
   }
   private bool Matches(DataRowView row, string term)
   {
    string[] fields = { "msds_msds_id", "msds_graco_code", "msds_product_name", "msds_supplier_name" };
    if (fields.Any(f => Value(row, f).Contains(term, StringComparison.OrdinalIgnoreCase))) return true;
    return long.TryParse(Value(row, "msds_msds_id"), out long id) &&
     _componentSearch.TryGetValue(id, out string? content) && content.Contains(term, StringComparison.OrdinalIgnoreCase);
   }
   private void ApplyFilter()
   {
    if (_master.Columns.Count == 0) return;
    string query = SearchText.Text.Trim();
    _ = new SearchParser(query, _ => false).Evaluate();
    string? dateColumn = ResolveDateColumn();
    DateTime? from = DateFrom.SelectedDate, to = DateTo.SelectedDate;
    if (dateColumn != null && from.HasValue && to.HasValue && from > to) throw new FormatException("시작일은 종료일보다 늦을 수 없습니다.");
    var matched = _master.DefaultView.Cast<DataRowView>().Where(row =>
     (dateColumn == null || (!from.HasValue && !to.HasValue) || DateMatches(Value(row, dateColumn), from, to)) &&
     (LatestOnly.IsChecked != true || IsLatest(row)) &&
     (TypeAll.IsChecked == true || (TypeFg.IsChecked == true && Value(row,"msds_material_type").Equals("FG", StringComparison.OrdinalIgnoreCase)) || (TypeRm.IsChecked == true && Value(row,"msds_material_type").Equals("RM", StringComparison.OrdinalIgnoreCase))) &&
     new SearchParser(query, term => Matches(row, term)).Evaluate()).ToList();
    // DataView.RowFilter cannot express multi-table AND/OR reliably; retain the complete master table.
    MasterGrid.ItemsSource = matched;
    MasterGrid.SelectedItems.Clear();
    ClearDetails();
    UpdateSelectionUi();
   }
   private void UpdateSelectionUi()
   {
    if (MasterGrid == null || CountText == null || DeleteButton == null) return;
    int shown = MasterGrid.ItemsSource is System.Collections.ICollection rows ? rows.Count : 0;
    int selected = MasterGrid.SelectedItems.Count;
    CountText.Text = $"조회 {shown}건 / 전체 {_master.Rows.Count}건 / 선택 {selected}건";
    DeleteButton.IsEnabled = !_editing && selected > 0;
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
   string dbPath=DatabaseManager.GetDbPath("msds");
   if(string.IsNullOrWhiteSpace(dbPath)||!File.Exists(dbPath)) throw new FileNotFoundException("QCS DB 파일이 존재하지 않습니다. DB 연결 상태를 확인하세요.",dbPath);
   var builder=new SqliteConnectionStringBuilder { DataSource=dbPath,Mode=SqliteOpenMode.ReadOnly,DefaultTimeout=10,Pooling=false };
   var connection=new SqliteConnection(builder.ToString()); connection.Open(); return connection;
  }
  private void RefreshData()
  {
   if (_editing) return;
   try
   {
    using var connection=OpenReadOnly();
    _master=ReadTable(connection,"SELECT * FROM msds_mstr ORDER BY msds_msds_id DESC");
    _componentSearch.Clear();
    var components = ReadTable(connection, "SELECT msds_comp_parent_msds_id, msds_comp_name, msds_comp_cas_no FROM msds_comp_dtl");
    foreach (DataRow component in components.Rows)
    {
     if (!long.TryParse(Convert.ToString(component["msds_comp_parent_msds_id"]), out long parentId)) continue;
     string entry = (Convert.ToString(component["msds_comp_name"]) ?? "") + " " + (Convert.ToString(component["msds_comp_cas_no"]) ?? "");
     _componentSearch[parentId] = (_componentSearch.TryGetValue(parentId, out string? existing) ? existing + " " : "") + entry;
    }
    DetailGrid.ItemsSource=null;
    ClearDetails(); ApplyFilter();
    StatusText.Text=$"DB: {DatabaseManager.GetDbPath("msds")} | 마지막 조회: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
    PublishStatus($"MSDS 조회 완료: {CountText.Text}");
   }
   catch(FormatException ex) { PublishStatus("MSDS 검색식 오류: " + ex.Message); MessageBox.Show(ex.Message,"검색식 오류",MessageBoxButton.OK,MessageBoxImage.Warning); }
   catch(Exception ex) { StatusText.Text="DB 조회 실패: "+ex.Message; PublishStatus("MSDS 조회 실패: " + ex.Message); MessageBox.Show(ex.Message,"MSDS DB 조회 오류",MessageBoxButton.OK,MessageBoxImage.Error); }
  }
   private void ClearDetails()
  {
   if (_editing) return;
    _detailId = null;
   foreach(var box in BasicTextBoxes()) box.Text="";
   DetailGrid.ItemsSource=null; DetailCount.Text="구성성분 (0건)";
   _selectedPdfPath=""; PdfFileName.Text="선택된 PDF 없음";
   _previewRequest++;
   if(PdfBrowser.CoreWebView2 != null) PdfBrowser.CoreWebView2.Navigate("about:blank");
   PreviewFallback.Visibility=Visibility.Visible;
   PreviewMessage.Text="MSDS를 선택하세요.";
  }
  // 수정 중에는 목록의 다른 행을 선택할 수 없지만 스크롤은 유지한다.
  private void MasterGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
  {
    if (!_editing) return;
    System.Windows.DependencyObject? node = e.OriginalSource as System.Windows.DependencyObject;
    while (node != null && node is not System.Windows.Controls.DataGridRow && node != MasterGrid)
      node = VisualTreeHelper.GetParent(node);
    if (node is System.Windows.Controls.DataGridRow row && !ReferenceEquals(row.Item, _lockedMasterRow)) e.Handled = true;
  }
  private void MasterGrid_PreviewKeyDown(object sender, KeyEventArgs e)
  {
    if (!_editing) return;
    if (e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Home || e.Key == Key.End ||
        e.Key == Key.PageUp || e.Key == Key.PageDown || e.Key == Key.Space ||
        (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0)) e.Handled = true;
  }
  private void MasterGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
  {
    if (_editing && !_restoringSelection)
    {
      if (MasterGrid.SelectedItems.Count != 1 || !ReferenceEquals(MasterGrid.SelectedItem, _lockedMasterRow))
      {
        _restoringSelection = true;
        try { MasterGrid.SelectedItems.Clear(); if (_lockedMasterRow != null) MasterGrid.SelectedItem = _lockedMasterRow; }
        finally { _restoringSelection = false; }
      }
      return;
    }
   UpdateSelectionUi();
   if(MasterGrid.SelectedItem is not DataRowView row || !long.TryParse(Value(row,"msds_msds_id"),out long id)) { ClearDetails(); return; }
   if (_detailId == id) return;
   _detailId = id;
   foreach(var box in BasicTextBoxes())
    if(box.Tag is string key) box.Text=key=="msds_is_latest" ? (IsLatest(row)?"예":"아니오") : key=="msds_material_type" ? (Value(row,key)=="RM" ? "원료(RM)" : Value(row,key)=="FG" ? "제품(FG)" : Value(row,key)) : Value(row,key);
   _selectedPdfPath=Value(row,"msds_file_path");
   PdfFileName.Text=Value(row,"msds_file_name");
   LoadPreview();
   try
   {
    using var connection=OpenReadOnly();
    var detail=ReadTable(connection,"SELECT * FROM msds_comp_dtl WHERE msds_comp_parent_msds_id = $id ORDER BY msds_comp_sort_order, msds_comp_msds_id",id);
    _detailTable=detail; detail.AcceptChanges(); DetailGrid.ItemsSource=detail.DefaultView; DetailCount.Text=$"구성성분 ({detail.Rows.Count}건) — MSDS ID: {id}";
   }
   catch(Exception ex) { DetailGrid.ItemsSource=null; DetailCount.Text="구성성분 조회 실패"; StatusText.Text="구성성분 조회 실패: "+ex.Message; }
  }
  private static readonly string[] CasReferenceTypes = { "인체", "사고", "제한", "중점", "잔류", "배출량" };
  private TextBox[] ReferenceBoxes => new[] { HumanReferenceBox, AccidentReferenceBox, RestrictReferenceBox, PriorityReferenceBox, PersistReferenceBox, EmissionReferenceBox };
  private void DetailGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadCasReferences();
  private void DetailGrid_CurrentCellChanged(object? sender, EventArgs e) => LoadCasReferences();
  private void LoadCasReferences()
  {
   foreach (var box in ReferenceBoxes) box.Clear();
   // CellOrRowHeader 모드에서는 SelectedItem이 null이어도 CurrentCell.Item은 선택된 성분입니다.
   var component = DetailGrid.CurrentCell.Item as DataRowView
    ?? DetailGrid.SelectedItem as DataRowView
    ?? DetailGrid.SelectedCells.Select(cell => cell.Item).OfType<DataRowView>().FirstOrDefault();
   if (component == null) { CasReferenceStatus.Text = "  ·  구성성분 행을 선택하세요"; return; }
   string cas = Value(component, "msds_comp_cas_no").Trim().Replace(" ", "");
   if (cas.Length == 0 || cas == "-" || cas.Equals("NA", StringComparison.OrdinalIgnoreCase) || cas.Equals("NA--", StringComparison.OrdinalIgnoreCase))
   { CasReferenceStatus.Text = "  ·  유효한 CAS 번호가 없습니다"; return; }
   CasReferenceStatus.Text = "  ·  CAS " + cas + " 조회 중";
   try
   {
    var builder = new SqliteConnectionStringBuilder { DataSource = DatabaseManager.GetDbPath("reference"), Mode = SqliteOpenMode.ReadOnly, Pooling = false };
    using var conn = new SqliteConnection(builder.ToString()); conn.Open();
    using var cmd = conn.CreateCommand();
    // 참조관리 화면과 동일한 casref_value 필드를 사용합니다. 동일 CAS/유형의 첫 레코드만 적용합니다.
    cmd.CommandText = @"SELECT casref_type, casref_value FROM casref_mstr
      WHERE REPLACE(TRIM(casref_cas_no), ' ', '') = $cas
        AND casref_type IN ('인체','사고','제한','중점','잔류','배출량')
      ORDER BY casref_id";
    cmd.Parameters.AddWithValue("$cas", cas);
    var found = new Dictionary<string, string>(StringComparer.Ordinal);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
     string type = reader.IsDBNull(0) ? "" : (Convert.ToString(reader.GetValue(0)) ?? "").Trim();
     if (!CasReferenceTypes.Contains(type) || found.ContainsKey(type)) continue;
     found[type] = reader.IsDBNull(1) ? "" : Convert.ToString(reader.GetValue(1)) ?? "";
    }
    for (int i = 0; i < CasReferenceTypes.Length; i++)
     ReferenceBoxes[i].Text = found.TryGetValue(CasReferenceTypes[i], out string? value)
      ? (string.IsNullOrWhiteSpace(value) ? "(참조자료 값 없음)" : value) : "(일치 자료 없음)";
    CasReferenceStatus.Text = $"  ·  CAS {cas} / 일치 유형 {found.Count}건";
   }
   catch (Exception ex)
   {
    CasReferenceStatus.Text = "  ·  조회 오류: " + ex.Message;
    PublishStatus("CAS 참조정보 조회 실패: " + ex.Message);
   }
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
    PdfBrowser.CoreWebView2.Navigate(new Uri(Path.GetFullPath(_selectedPdfPath)).AbsoluteUri);
    PreviewFallback.Visibility=Visibility.Collapsed;
   }
   catch(Exception ex) { PreviewMessage.Text="PDF 미리보기 오류: " + ex.Message; PreviewFallback.Visibility=Visibility.Visible; }
  }
   // 접기 전에 사용자가 조절한 실제 너비를 저장하고, 펼칠 때 복원한다.
   // Star 비율을 축소된 영역에 재적용하면 0에 가까운 폭이 되는 현상을 방지한다.
   private double _savedFilesWidth;
   private double _savedPreviewWidth;

   private void TogglePreview_Click(object sender, RoutedEventArgs e)
   {
    var columns = WorkspaceGrid.ColumnDefinitions;
    if (_previewExpanded)
    {
     _savedPreviewWidth = Math.Max(0, columns[4].ActualWidth);
     _previewExpanded = false;
     PreviewPanel.Visibility = Visibility.Collapsed;
     RightSplitter.Visibility = Visibility.Collapsed;
     columns[4].Width = new GridLength(0);
     columns[3].Width = new GridLength(0);
    }
    else
    {
     _previewExpanded = true;
     columns[3].Width = new GridLength(6);
     double available = Math.Max(0, WorkspaceGrid.ActualWidth - 12);
     double target = _savedPreviewWidth > 100 ? _savedPreviewWidth : available * 0.30;
     columns[4].Width = new GridLength(Math.Min(target, available * 0.65));
     PreviewPanel.Visibility = Visibility.Visible;
     RightSplitter.Visibility = Visibility.Visible;
     LoadPreview();
    }
    TogglePreviewButton.Content = _previewExpanded ? "▣  원문 접기" : "▣  원문 보기";
    PublishStatus(_previewExpanded ? "PDF 원문 표시" : "PDF 원문 감춤");
   }

   private void ToggleFiles_Click(object sender, RoutedEventArgs e)
   {
    var columns = WorkspaceGrid.ColumnDefinitions;
    if (_filesExpanded)
    {
     _savedFilesWidth = Math.Max(0, columns[0].ActualWidth);
     _filesExpanded = false;
     FilesPanel.Visibility = Visibility.Collapsed;
     LeftSplitter.Visibility = Visibility.Collapsed;
     columns[0].Width = new GridLength(0);
     columns[1].Width = new GridLength(0);
    }
    else
    {
     _filesExpanded = true;
     columns[1].Width = new GridLength(6);
     double available = Math.Max(0, WorkspaceGrid.ActualWidth - 12);
     double target = _savedFilesWidth > 100 ? _savedFilesWidth : available * 0.25;
     columns[0].Width = new GridLength(Math.Min(target, available * 0.65));
     FilesPanel.Visibility = Visibility.Visible;
     LeftSplitter.Visibility = Visibility.Visible;
    }
    ToggleFilesButton.Content = _filesExpanded ? "☰  목록 접기" : "☰  MSDS 목록";
    PublishStatus(_filesExpanded ? "MSDS 목록 펼침" : "MSDS 목록 접음");
   }
   private bool ConfirmDiscard()
   {
     if (!_editing) return true;
     if (MessageBox.Show("저장하지 않은 변경사항을 취소하시겠습니까?", "MSDS 수정 취소", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
     ExitEditMode();
     return true;
   }
   private void SetEditMode(bool enabled)
   {
     _editing = enabled;
     EditButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
     SaveEditButton.Visibility = CancelEditButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
     AddDetailButton.IsEnabled = RemoveDetailButton.IsEnabled = enabled;
     DetailGrid.IsReadOnly = !enabled;
     DetailGrid.CanUserAddRows = false;
     DetailGrid.CanUserDeleteRows = false;
     foreach (var box in BasicTextBoxes())
       if (box.Tag is string key)
         box.IsReadOnly = !enabled || key == "msds_msds_id" || key == "msds_file_name" || key == "msds_material_type";
     SearchText.IsEnabled = LatestOnly.IsEnabled = SearchButton.IsEnabled = !enabled;
     TypeAll.IsEnabled = TypeFg.IsEnabled = TypeRm.IsEnabled = !enabled;
     DateField.IsEnabled = DateFrom.IsEnabled = DateTo.IsEnabled = !enabled;
     var normalBg = (SolidColorBrush)new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString("#EAF3FF"));
     var editableBg = (SolidColorBrush)new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString("#FFFFFF"));
     foreach (var box in BasicTextBoxes())
       if (box.Tag is string key)
       {
         bool locked = key == "msds_msds_id" || key == "msds_file_name" || key == "msds_material_type";
         box.Background = enabled && !locked ? editableBg : normalBg;
         box.Foreground = enabled && !locked ? System.Windows.Media.Brushes.Black : new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString("#1E3A5F"));
         box.BorderBrush = enabled && !locked ? new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString("#8DB4E8")) : new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString("#CED8E5"));
       }
     DetailGrid.RowBackground = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(enabled ? "#FFFFFF" : "#F0F6FF"));
     DetailGrid.AlternatingRowBackground = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(enabled ? "#F7FAFE" : "#EAF3FF"));
     DeleteButton.IsEnabled = !enabled && MasterGrid.SelectedItems.Count > 0;
   }
   private void ExitEditMode()
   {
     _editingId = null;
     _lockedMasterRow = null;
     SetEditMode(false);
   }
   private void Edit_Click(object sender, RoutedEventArgs e)
   {
     if (_detailId == null || MasterGrid.SelectedItems.Count != 1) { PublishStatus("수정할 MSDS 한 건을 선택하세요."); return; }
     _editingId = _detailId;
     _lockedMasterRow = MasterGrid.SelectedItem as DataRowView;
     _detailTable?.AcceptChanges();
     SetEditMode(true);
     PublishStatus($"MSDS ID {_editingId}: 수정 모드");
   }
   private void CancelEdit_Click(object sender, RoutedEventArgs e)
   {
     if (!ConfirmDiscard()) return;
     var id = _detailId;
     _detailId = null;
     if (MasterGrid.SelectedItem is DataRowView row) MasterGrid_SelectionChanged(MasterGrid, new SelectionChangedEventArgs(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new System.Collections.ArrayList(), new System.Collections.ArrayList()));
     PublishStatus($"MSDS ID {id}: 수정 취소");
   }
   private void AddDetail_Click(object sender, RoutedEventArgs e)
   {
     if (!_editing || _detailTable == null) return;
     DetailGrid.CommitEdit(DataGridEditingUnit.Cell, true);
     DetailGrid.CommitEdit(DataGridEditingUnit.Row, true);
     var row = _detailTable.NewRow();
     if (_detailTable.Columns.Contains("msds_comp_parent_msds_id")) row["msds_comp_parent_msds_id"] = _editingId!.Value.ToString();
     if (_detailTable.Columns.Contains("msds_comp_sort_order")) row["msds_comp_sort_order"] = (_detailTable.Rows.Count + 1).ToString();
     _detailTable.Rows.Add(row);
     DetailGrid.SelectedItem = _detailTable.DefaultView[_detailTable.DefaultView.Count - 1];
     DetailGrid.ScrollIntoView(DetailGrid.SelectedItem);
     DetailCount.Text = $"구성성분 ({_detailTable.DefaultView.Count}건) — MSDS ID: {_editingId}";
   }
   private void RemoveDetail_Click(object sender, RoutedEventArgs e)
   {
     if (!_editing || _detailTable == null) return;
     DetailGrid.CommitEdit(DataGridEditingUnit.Cell, true);
     DetailGrid.CommitEdit(DataGridEditingUnit.Row, true);
     var selected = DetailGrid.SelectedCells.Select(c => c.Item).Concat(DetailGrid.SelectedItems.Cast<object>()).OfType<DataRowView>().Distinct().ToList();
     if (selected.Count == 0) { PublishStatus("삭제할 구성성분 행을 선택하세요."); return; }
     foreach (var row in selected) row.Row.Delete();
     DetailCount.Text = $"구성성분 ({_detailTable.DefaultView.Count}건) — MSDS ID: {_editingId}";
   }
   private void DetailGrid_PreviewKeyDown(object sender, KeyEventArgs e)
   {
     if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
     if (!_editing || _detailTable == null) { e.Handled = true; return; }
     e.Handled = true;
     DetailGrid.CommitEdit(DataGridEditingUnit.Cell, true);
     DetailGrid.CommitEdit(DataGridEditingUnit.Row, true);
     string content = Clipboard.ContainsText() ? Clipboard.GetText() : "";
     if (Clipboard.ContainsData(DataFormats.Html))
     {
       var html = Clipboard.GetData(DataFormats.Html) as string;
       if (!string.IsNullOrWhiteSpace(html))
       {
         var rows = Regex.Matches(html, @"<tr\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
         var parsed = new List<string>();
         foreach (Match tr in rows)
         {
           var cells = Regex.Matches(tr.Groups[1].Value, @"<t[dh]\b[^>]*>(.*?)</t[dh]>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
           if (cells.Count == 0) continue;
           parsed.Add(string.Join("\t", cells.Cast<Match>().Select(m => System.Net.WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, @"<[^>]+>", "")).Trim())));
         }
         if (parsed.Count > 0) content = string.Join("\n", parsed);
       }
     }
     if (string.IsNullOrWhiteSpace(content)) return;
     var lines = content.Replace("\r\n", "\n").Replace('\r','\n').TrimEnd('\n').Split('\n');
     var columns = DetailGrid.Columns.OrderBy(c => c.DisplayIndex).OfType<DataGridBoundColumn>().ToList();
     var startColumn = DetailGrid.CurrentCell.Column == null ? 0 : DetailGrid.CurrentCell.Column.DisplayIndex;
     int startRow = DetailGrid.SelectedIndex;
     if (startRow < 0 && DetailGrid.CurrentItem != null) startRow = DetailGrid.Items.IndexOf(DetailGrid.CurrentItem);
     if (startRow < 0) startRow = _detailTable.DefaultView.Count;
     for (int i=0;i<lines.Length;i++)
     {
       var cells=lines[i].Split('\t');
       while (_detailTable.DefaultView.Count <= startRow+i)
       {
         var row=_detailTable.NewRow();
         if (_detailTable.Columns.Contains("msds_comp_parent_msds_id")) row["msds_comp_parent_msds_id"]=_editingId!.Value.ToString();
         if (_detailTable.Columns.Contains("msds_comp_sort_order")) row["msds_comp_sort_order"]=(_detailTable.Rows.Count+1).ToString();
         _detailTable.Rows.Add(row);
       }
       var target=_detailTable.DefaultView[startRow+i];
       for(int j=0;j<cells.Length && startColumn+j<columns.Count;j++)
       {
         if (columns[startColumn+j].Binding is Binding binding && binding.Path?.Path is string path)
         {
           var key=path.Trim('[',']');
           if (_detailTable.Columns.Contains(key)) target[key]=cells[j];
         }
       }
     }
     DetailCount.Text=$"구성성분 ({_detailTable.DefaultView.Count}건) — MSDS ID: {_editingId}";
   }
   private static HashSet<string> GetDbColumns(SqliteConnection conn, string table)
   {
     using var cmd=conn.CreateCommand(); cmd.CommandText=$"PRAGMA table_info({table})";
     using var reader=cmd.ExecuteReader(); var cols=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
     while(reader.Read()) cols.Add(reader.GetString(1));
     return cols;
   }
   private static Dictionary<string, object?> ReadAuditMaster(SqliteConnection conn, SqliteTransaction tx, long id)
   {
    using var cmd=conn.CreateCommand();cmd.Transaction=tx;
    cmd.CommandText="SELECT * FROM msds_mstr WHERE msds_msds_id=$id";cmd.Parameters.AddWithValue("$id",id);
    using var reader=cmd.ExecuteReader();
    var data=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
    if(reader.Read())for(int i=0;i<reader.FieldCount;i++)data[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);
    return data;
   }
   private static Dictionary<long, Dictionary<string,object?>> ReadAuditComponents(SqliteConnection conn, SqliteTransaction tx, long id)
   {
    using var cmd=conn.CreateCommand();cmd.Transaction=tx;
    cmd.CommandText="SELECT * FROM msds_comp_dtl WHERE msds_comp_parent_msds_id=$id";cmd.Parameters.AddWithValue("$id",id);
    using var reader=cmd.ExecuteReader();
    var rows=new Dictionary<long,Dictionary<string,object?>>();
    while(reader.Read())
    {
     var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
     for(int i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);
     rows[Convert.ToInt64(row["msds_comp_msds_id"])]=row;
    }
    return rows;
   }
   private static void AddAuditDiff(List<object> changes,string table,long recordId,Dictionary<string,object?> before,Dictionary<string,object?> after)
   {
    foreach(var key in before.Keys.Union(after.Keys))
    {
     before.TryGetValue(key,out var oldValue);after.TryGetValue(key,out var newValue);
     if(string.Equals(Convert.ToString(oldValue,CultureInfo.InvariantCulture),Convert.ToString(newValue,CultureInfo.InvariantCulture),StringComparison.Ordinal))continue;
     changes.Add(new { table,record_id=recordId,field=key,old=oldValue,@new=newValue });
    }
   }
   private void SaveEdit_Click(object sender, RoutedEventArgs e)
   {
     if (!_editing || !_editingId.HasValue || _detailTable == null) return;
     DetailGrid.CommitEdit(DataGridEditingUnit.Cell,true);
     DetailGrid.CommitEdit(DataGridEditingUnit.Row,true);
     if (MessageBox.Show("기본정보 및 구성성분 변경사항을 DB에 저장하시겠습니까?", "MSDS 수정 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { PublishStatus("MSDS 수정 저장 취소"); return; }
     long id=_editingId.Value;
     try
     {
       string dbPath=DatabaseManager.GetDbPath("msds");
       var builder=new SqliteConnectionStringBuilder { DataSource=dbPath, Mode=SqliteOpenMode.ReadWrite, DefaultTimeout=10, Pooling=false };
       using var conn=new SqliteConnection(builder.ToString()); conn.Open();
       using (var pragma=conn.CreateCommand()) { pragma.CommandText="PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; pragma.ExecuteNonQuery(); }
       var pendingAuditRevision=MsdsDbRegistration.GetPendingRevisionForAudit();
       var masterColumns=GetDbColumns(conn,"msds_mstr");
       var detailColumns=GetDbColumns(conn,"msds_comp_dtl");
       using var tx=conn.BeginTransaction();
       var beforeMaster=ReadAuditMaster(conn,tx,id);
       var beforeComponents=ReadAuditComponents(conn,tx,id);
       var fields=BasicTextBoxes().Where(b=>b.Tag is string key && key!="msds_msds_id" && key!="msds_file_name" && key!="msds_material_type" && masterColumns.Contains(key)).ToList();
       using (var cmd=conn.CreateCommand())
       {
         cmd.Transaction=tx;
         var sets=new List<string>();
         foreach(var box in fields)
         {
           string key=(string)box.Tag;
           sets.Add($"\"{key}\"=@{key}");
           cmd.Parameters.AddWithValue("@"+key, key=="msds_is_latest" ? (box.Text.Trim()=="예" || box.Text.Trim()=="1" ? "1":"0") : box.Text);
         }
         if (sets.Count>0)
         {
           cmd.CommandText="UPDATE msds_mstr SET "+string.Join(",",sets)+" WHERE msds_msds_id=@id";
           cmd.Parameters.AddWithValue("@id",id);
           if(cmd.ExecuteNonQuery()!=1) throw new InvalidOperationException("MSDS 기본정보 저장 대상이 정확히 1건이 아닙니다.");
         }
       }
       string pk="msds_comp_msds_id";
       if (!detailColumns.Contains(pk)) throw new InvalidOperationException("구성성분 고유 ID 컬럼을 확인할 수 없습니다.");
       foreach(DataRow row in _detailTable.Rows)
       {
         if(row.RowState==DataRowState.Unchanged) continue;
         using var cmd=conn.CreateCommand();cmd.Transaction=tx;
         if(row.RowState==DataRowState.Deleted)
         {
           cmd.CommandText=$"DELETE FROM msds_comp_dtl WHERE \"{pk}\"=@pk AND msds_comp_parent_msds_id=@parent";
           cmd.Parameters.AddWithValue("@pk",row[pk,DataRowVersion.Original]);cmd.Parameters.AddWithValue("@parent",id);
           if(cmd.ExecuteNonQuery()!=1) throw new InvalidOperationException("구성성분 삭제 대상이 변경되었습니다.");
           continue;
         }
         var editable=DetailGrid.Columns.OfType<DataGridBoundColumn>().Select(c=>(c.Binding as Binding)?.Path?.Path?.Trim('[',']')).OfType<string>().Where(c=>!string.IsNullOrWhiteSpace(c) && detailColumns.Contains(c)).Distinct().ToList();
         if(row.RowState==DataRowState.Added)
         {
           var keys=editable.Concat(new[]{"msds_comp_parent_msds_id","msds_comp_sort_order"}).Where(detailColumns.Contains).Distinct().ToList();
           cmd.CommandText="INSERT INTO msds_comp_dtl ("+string.Join(",",keys.Select(k=>"\""+k+"\""))+") VALUES ("+string.Join(",",keys.Select((k,i)=>"@p"+i))+")";
           for(int i=0;i<keys.Count;i++)cmd.Parameters.AddWithValue("@p"+i,keys[i]=="msds_comp_parent_msds_id" ? id : row[keys[i]] is DBNull ? DBNull.Value : row[keys[i]]);
         }
         else
         {
           cmd.CommandText="UPDATE msds_comp_dtl SET "+string.Join(",",editable.Select((k,i)=>"\""+k+"\"=@p"+i))+" WHERE \""+pk+"\"=@pk AND msds_comp_parent_msds_id=@parent";
           for(int i=0;i<editable.Count;i++)cmd.Parameters.AddWithValue("@p"+i,row[editable[i]!]);
           cmd.Parameters.AddWithValue("@pk",row[pk]);cmd.Parameters.AddWithValue("@parent",id);
         }
         if(cmd.ExecuteNonQuery()!=1) throw new InvalidOperationException("구성성분 저장 건수가 일치하지 않습니다.");
       }
       var afterMaster=ReadAuditMaster(conn,tx,id);
       var afterComponents=ReadAuditComponents(conn,tx,id);
       var changes=new List<object>();
       AddAuditDiff(changes,"msds_mstr",id,beforeMaster,afterMaster);
       foreach(var componentId in beforeComponents.Keys.Union(afterComponents.Keys))
       {
        beforeComponents.TryGetValue(componentId,out var oldRow);
        afterComponents.TryGetValue(componentId,out var newRow);
        AddAuditDiff(changes,"msds_comp_dtl",componentId,
          oldRow??new Dictionary<string,object?>(),newRow??new Dictionary<string,object?>());
       }
       if(changes.Count>0)
       {
        var required=new[]{"audit_log_msds_id","audit_log_product_name","audit_log_content","audit_log_user_id","audit_log_action","audit_log_datetime","audit_log_graco_code","audit_log_rev_no"};
        var auditCols=GetDbColumns(conn,"audit_log_hist");
        if(required.Any(k=>!auditCols.Contains(k)))throw new InvalidOperationException("Audit DB 스키마에 필요한 컬럼이 없습니다.");
        using var audit=conn.CreateCommand();audit.Transaction=tx;
        audit.CommandText=@"INSERT INTO audit_log_hist
         (audit_log_msds_id,audit_log_product_name,audit_log_content,audit_log_user_id,audit_log_action,audit_log_datetime,audit_log_graco_code,audit_log_rev_no)
         VALUES ($id,$name,$content,$user,'UPDATE',$date,$code,$rev)";
        audit.Parameters.AddWithValue("$id",id);
        audit.Parameters.AddWithValue("$name",afterMaster.GetValueOrDefault("msds_product_name")??DBNull.Value);
        audit.Parameters.AddWithValue("$content",JsonSerializer.Serialize(new { registration_type="EDIT",changes }));
        audit.Parameters.AddWithValue("$user",Environment.UserName);
        audit.Parameters.AddWithValue("$date",DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        audit.Parameters.AddWithValue("$code",afterMaster.GetValueOrDefault("msds_graco_code")??DBNull.Value);
        audit.Parameters.AddWithValue("$rev", pendingAuditRevision);
        audit.ExecuteNonQuery();
       }
       tx.Commit();
       ExitEditMode();
       RefreshData();
       var matching=MasterGrid.Items.Cast<DataRowView>().FirstOrDefault(r=>Value(r,"msds_msds_id")==id.ToString());
       if(matching!=null) MasterGrid.SelectedItem=matching;
       PublishStatus($"MSDS ID {id}: 수정 저장 완료");
     }
     catch(Exception ex) { PublishStatus("MSDS 수정 저장 실패: "+ex.Message); MessageBox.Show(ex.Message,"MSDS 저장 오류",MessageBoxButton.OK,MessageBoxImage.Error); }
   }

  private void OpenPdf_Click(object sender,RoutedEventArgs e)
  {
   if(string.IsNullOrWhiteSpace(_selectedPdfPath)||!File.Exists(_selectedPdfPath)) { MessageBox.Show("원본 PDF 파일을 찾을 수 없습니다.","PDF 열기",MessageBoxButton.OK,MessageBoxImage.Warning); return; }
   try { Process.Start(new ProcessStartInfo { FileName=_selectedPdfPath,UseShellExecute=true }); }
   catch(Exception ex) { MessageBox.Show(ex.Message,"PDF 열기 실패",MessageBoxButton.OK,MessageBoxImage.Error); }
  }
   private void DeleteSelected_Click(object sender, RoutedEventArgs e)
   {
    if (!ConfirmDiscard()) return;
    var selected = MasterGrid.SelectedItems.Cast<DataRowView>()
     .Select(row => new { IdText = Value(row, "msds_msds_id"), Name = Value(row, "msds_file_name"), Path = Value(row, "msds_file_path") })
     .ToList();
    if (selected.Count == 0) { PublishStatus("삭제할 MSDS를 선택하세요."); return; }
    if (selected.Any(row => !long.TryParse(row.IdText, out _)))
    { PublishStatus("MSDS 삭제 제한: 유효하지 않은 ID가 있습니다."); return; }
    if (MessageBox.Show($"선택한 MSDS {selected.Count}건을 DB에서 영구 삭제하시겠습니까?\n\n" +
      "관련 구성성분·규제·추출 결과 및 등록 이력도 삭제됩니다.\n" +
      "AI 학습·적용이력 또는 다른 MSDS에서 참조하는 PDF는 보존합니다. 미참조 PDF만 삭제를 시도합니다.",
      "MSDS 일괄 삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
    { PublishStatus($"MSDS {selected.Count}건 삭제 취소"); return; }
    try
    {
     string dbPath = DatabaseManager.GetDbPath("msds");
     if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath)) throw new FileNotFoundException("DB 파일을 찾을 수 없습니다.", dbPath);
     var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, DefaultTimeout = 10, Pooling = false };
     using var connection = new SqliteConnection(builder.ToString());
     connection.Open();
     using (var pragma = connection.CreateCommand()) { pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; pragma.ExecuteNonQuery(); }
     using (var transaction = connection.BeginTransaction())
     {
      string[] statements = {
       "DELETE FROM reg_result_dtl WHERE reg_result_msds_id=$id",
       "DELETE FROM extract_result_dtl WHERE extract_result_msds_id=$id",
       "DELETE FROM msds_comp_dtl WHERE msds_comp_parent_msds_id=$id",
       "DELETE FROM msds_oper_dtl WHERE msds_oper_parent_msds_id=$id",
       "DELETE FROM audit_log_hist WHERE audit_log_msds_id=$id",
       "DELETE FROM msds_mstr WHERE msds_msds_id=$id"
      };
      foreach (var row in selected)
      {
       long id = long.Parse(row.IdText);
       foreach (string sql in statements)
       {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        int count = cmd.ExecuteNonQuery();
        if (sql.StartsWith("DELETE FROM msds_mstr", StringComparison.Ordinal) && count != 1)
         throw new InvalidOperationException($"MSDS ID {id}: 삭제 대상이 없거나 이미 삭제되었습니다.");
       }
      }
      transaction.Commit();
     }
     var warnings = new List<string>();
     int preserved = 0;
     foreach (var path in selected.Select(row => row.Path).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
     {
      try {
       // Fail closed when reference lookup fails. Hold the write reservation during the physical deletion.
       using var guard = connection.BeginTransaction();
       using var check = connection.CreateCommand(); check.Transaction = guard;
       check.CommandText = @"SELECT EXISTS(SELECT 1 FROM ai_correctionh_hist WHERE ai_correctionh_pdf_path=$path COLLATE NOCASE UNION ALL SELECT 1 FROM ai_applyh_hist WHERE ai_applyh_pdf_path=$path COLLATE NOCASE UNION ALL SELECT 1 FROM msds_mstr WHERE msds_file_path=$path COLLATE NOCASE)";
       check.Parameters.AddWithValue("$path",path);
       if (Convert.ToInt64(check.ExecuteScalar()) != 0) preserved++;
       else if (File.Exists(path)) File.Delete(path);
       guard.Commit();
      }
      catch (Exception ex) { warnings.Add(Path.GetFileName(path) + ": " + ex.Message); }
     }
     RefreshData();
     string message = $"MSDS {selected.Count}건 삭제 완료 (PDF 보존 {preserved}건)" + (warnings.Count > 0 ? $" (PDF 삭제 실패 {warnings.Count}건)" : "");
     StatusText.Text = message;
     PublishStatus(message);
     if (warnings.Count > 0) MessageBox.Show(string.Join("\n", warnings), "PDF 삭제 안내", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    catch (Exception ex)
    {
     StatusText.Text = "DB 레코드 삭제 실패: " + ex.Message;
     PublishStatus("MSDS 삭제 실패: " + ex.Message);
     MessageBox.Show(ex.Message, "MSDS DB 삭제 오류", MessageBoxButton.OK, MessageBoxImage.Error);
    }
   }
 }
}
