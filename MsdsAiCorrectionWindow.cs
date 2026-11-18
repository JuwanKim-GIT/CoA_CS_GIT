using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using QCS.Analysis;
using WpfMessageBox = System.Windows.MessageBox;
using WpfButton = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace QCS
{
    // PDF 한 건의 필드 교정: 추천 후보, 전체 원문 검색, 직접 입력.
    public sealed class MsdsAiCorrectionWindow : Window
    {
        private sealed class FieldRow
        {
            public string Key { get; init; } = "";
            public string Name { get; init; } = "";
            public string Original { get; init; } = "";
            public string Value { get; set; } = "";
            public string Status => string.IsNullOrWhiteSpace(Value) ? "누락" :
                Value != Original ? "수정" : "유지";
        }
        private sealed class Candidate
        {
            public string Value { get; init; } = "";
            public string Source { get; init; } = "";
            public int Page { get; init; }
            public int Rank { get; init; }
        }
        private static readonly Dictionary<string, string> Names = new()
        {
            ["ProductNameBox"]="제품명", ["SupplierNameBox"]="공급업체", ["GracoCodeBox"]="GRACO 코드",
            ["RevisionDateBox"]="개정일", ["RevisionNumberBox"]="개정번호", ["PhoneBox"]="전화번호",
            ["AddressBox"]="주소", ["UnNumberBox"]="UN 번호", ["HazardCodesBox"]="유해위험 문구",
            ["AutoIgnitionBox"]="자연발화온도", ["VaporPressureBox"]="증기압", ["LelBox"]="폭발하한",
            ["UelBox"]="폭발상한", ["FlashPointBox"]="인화점", ["CorrosivityBox"]="부식성",
            ["PersonalProtectionBox"]="보호구", ["MoelSubmissionBox"]="제출번호"
        };
        private static readonly Dictionary<string, string[]> Hints = new()
        {
            ["ProductNameBox"] = new[]{"제품명","product name","product identifier"},
            ["SupplierNameBox"] = new[]{"공급자","제조사","supplier","manufacturer"},
            ["RevisionDateBox"] = new[]{"개정일","revision date","revised"},
            ["RevisionNumberBox"] = new[]{"개정번호","revision no","version"},
            ["PhoneBox"] = new[]{"전화번호","telephone","phone","tel"},
            ["AddressBox"] = new[]{"주소","address"},
            ["UnNumberBox"] = new[]{"un number","un no","유엔번호","un "},
            ["HazardCodesBox"] = new[]{"유해","hazard","h-code"},
            ["AutoIgnitionBox"] = new[]{"자연발화","auto-ignition","autoignition"},
            ["VaporPressureBox"] = new[]{"증기압","vapor pressure"},
            ["LelBox"] = new[]{"폭발하한","lower explosive","lel"},
            ["UelBox"] = new[]{"폭발상한","upper explosive","uel"},
            ["FlashPointBox"] = new[]{"인화점","flash point"},
            ["CorrosivityBox"] = new[]{"부식성","corrosive"},
            ["PersonalProtectionBox"] = new[]{"보호구","personal protection","ppe"},
            ["MoelSubmissionBox"] = new[]{"제출번호","submission number"}
        };
        private readonly string _fileName;
        private readonly string _filePath;
        private readonly MsdsPdfTextResult _pdf;
        private readonly MsdsAnalysisResult _analysis;
        private readonly ObservableCollection<FieldRow> _rows = new();
        private readonly ListBox _fields = new();
        private readonly ListBox _candidates = new();
        private readonly TextBox _search = new();
        private readonly TextBox _editor = new();
        private readonly TextBox _evidence = new();
        private readonly TextBlock _original = new();
        private readonly TextBlock _notice = new();
        private readonly CheckBox _more = new() { Content = "추가 후보 보기 (전체 파싱 원문)", Margin = new Thickness(0,8,0,5) };
        private bool _changing;
        public Dictionary<string, string> CorrectedValues { get; } = new();

        public MsdsAiCorrectionWindow(string fileName, string filePath, MsdsPdfTextResult pdf,
            MsdsAnalysisResult analysis, Dictionary<string,string> currentValues, IEnumerable<string> keys)
        {
            _fileName = fileName; _filePath = filePath; _pdf = pdf; _analysis = analysis;
            Title = "QCS - AI 분석 교정: " + fileName;
            Width = 1380; Height = 830; MinWidth = 1000; MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brushes.White;
            foreach (var key in keys)
            {
                var value = currentValues.TryGetValue(key, out var v) ? v : "";
                _rows.Add(new FieldRow { Key = key, Name = Names.TryGetValue(key,out var n) ? n : key,
                    Original = value, Value = value });
            }
            BuildUi();
            _fields.ItemsSource = _rows;
            _fields.SelectedIndex = 0;
        }
        private static TextBlock Text(string text, int size = 13, bool bold = false) => new()
        {
            Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(Color.FromRgb(30,58,95)), Margin = new Thickness(0,5,0,6),
            TextWrapping = TextWrapping.Wrap
        };
        private static WpfButton Button(string label, RoutedEventHandler click, bool primary = false)
        {
            var button = new WpfButton { Content = label, MinWidth = 115, Height = 36,
                Padding = new Thickness(12,4,12,4), Margin = new Thickness(5,0,0,0) };
            button.SetResourceReference(StyleProperty, primary ? "QcsPrimaryButton" : "QcsActionButton");
            button.Click += click;
            return button;
        }
        private void BuildUi()
        {
            var root = new DockPanel { Margin = new Thickness(14) };
            Content = root;
            var footer = new DockPanel { Height = 55, LastChildFill = true };
            DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(Button("취소", (s,e)=>Close()));
            actions.Children.Add(Button("교정값 적용", Apply, true));
            DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions);
            _notice.VerticalAlignment = VerticalAlignment.Center;
            _notice.Text = "선택한 교정값은 등록 화면에 반영되며, 기존 [저장] / [DB 저장] 절차는 유지됩니다.";
            _notice.Foreground = Brushes.DimGray;
            footer.Children.Add(_notice);
            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(470) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) });
            root.Children.Add(columns);
            var left = new DockPanel { Margin = new Thickness(0,0,8,0) };
            Grid.SetColumn(left,0); columns.Children.Add(left);
            var heading = Text("분석 항목 (문서 전체)",15,true);
            DockPanel.SetDock(heading,Dock.Top);left.Children.Add(heading);
            _fields.DisplayMemberPath = "Name";
            _fields.SelectionChanged += (_,__) => ShowField();
            left.Children.Add(_fields);
            AddSplitter(columns,1);AddSplitter(columns,3);
            var center = new DockPanel { Margin = new Thickness(12,0,12,0) };
            Grid.SetColumn(center,2);columns.Children.Add(center);
            var title = Text("교정값 검색 및 선택",15,true);
            DockPanel.SetDock(title,Dock.Top);center.Children.Add(title);
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            center.Children.Add(scroll);
            var stack = new StackPanel();scroll.Content=stack;
            stack.Children.Add(Text("기존 분석값",13,true));
            _original.TextWrapping = TextWrapping.Wrap;_original.Margin=new Thickness(0,0,0,12);
            stack.Children.Add(_original);
            stack.Children.Add(Text("AI 분석값 및 원문 문맥 기반 추천 후보",13,true));
            _candidates.Height=225;
            _candidates.SelectionChanged += (_,__) =>
            {
                if (_changing || _candidates.SelectedItem is not Candidate candidate) return;
                _editor.Text = candidate.Value;
                ShowEvidence(candidate.Page, candidate.Source);
            };
            stack.Children.Add(_candidates);
            _more.Checked += (_,__)=> RefreshCandidates();
            _more.Unchecked += (_,__)=> RefreshCandidates();
            stack.Children.Add(_more);
            stack.Children.Add(Text("파싱 원문 검색 (키워드 또는 값)",13,true));
            _search.Height=36;_search.Margin=new Thickness(0,0,0,8);
            _search.TextChanged += (_,__)=>RefreshCandidates();
            stack.Children.Add(_search);
            stack.Children.Add(Text("최종 교정값 (직접 수정 가능)",13,true));
            _editor.Height=90;_editor.AcceptsReturn=true;_editor.TextWrapping=TextWrapping.Wrap;
            _editor.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
            _editor.TextChanged += (_,__) =>
            {
                if (_changing || _fields.SelectedItem is not FieldRow row) return;
                row.Value=_editor.Text;
            };
            stack.Children.Add(_editor);
            stack.Children.Add(Text("※ 추천 후보는 원문 문맥에 따른 탐색 결과이며 정확도를 보증하지 않습니다.",11));
            var right = new DockPanel { Margin = new Thickness(10,0,0,0) };
            Grid.SetColumn(right,4);columns.Children.Add(right);
            var rightTitle=Text("PDF 파싱 원문 / 근거 확인",15,true);
            DockPanel.SetDock(rightTitle,Dock.Top);right.Children.Add(rightTitle);
            var open = Button("원본 PDF 열기",(s,e)=>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_filePath) { UseShellExecute=true }); }
                catch(Exception ex) { WpfMessageBox.Show("PDF 열기 실패: " + ex.Message); }
            });
            DockPanel.SetDock(open,Dock.Top);right.Children.Add(open);
            _evidence.IsReadOnly=true;_evidence.TextWrapping=TextWrapping.Wrap;
            _evidence.AcceptsReturn=true;_evidence.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
            _evidence.Margin=new Thickness(0,10,0,0);
            right.Children.Add(_evidence);
        }
        private static void AddSplitter(Grid grid,int col)
        {
            var splitter = new GridSplitter { Width=5,Background=new SolidColorBrush(Color.FromRgb(226,234,245)),
                HorizontalAlignment=HorizontalAlignment.Stretch };
            Grid.SetColumn(splitter,col);grid.Children.Add(splitter);
        }
        private void ShowField()
        {
            if (_fields.SelectedItem is not FieldRow row) return;
            _changing=true;
            _original.Text=string.IsNullOrWhiteSpace(row.Original)?"(추출값 없음)":row.Original;
            _editor.Text=row.Value;
            _search.Text="";
            _more.IsChecked=false;
            _changing=false;
            RefreshCandidates();
        }
        private void RefreshCandidates()
        {
            if (_fields.SelectedItem is not FieldRow row) return;
            var hints=Hints.TryGetValue(row.Key,out var h)?h:Array.Empty<string>();
            var query=_search.Text.Trim();
            var result=new List<Candidate>();
            var existing=_analysis.Fields.FirstOrDefault(f=>MapField(f.FieldKey)==row.Key);
            if (!string.IsNullOrWhiteSpace(existing?.Value))
                result.Add(new Candidate { Value=existing.Value!,Source=existing.SourceText??"AI 분석 결과",Page=existing.SourcePage??0,Rank=100 });
            foreach (var page in _pdf.Pages)
            {
                var lines=page.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries);
                for (int i=0;i<lines.Length;i++)
                {
                    var line=lines[i].Trim();
                    if (line.Length<2)continue;
                    var matched=hints.Any(x=>line.Contains(x,StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(query) && !line.Contains(query,StringComparison.OrdinalIgnoreCase))continue;
                    if (string.IsNullOrEmpty(query) && !matched && _more.IsChecked!=true)continue;
                    string candidate=line;
                    if (matched)
                    {
                        var cut=line.IndexOf(':');
                        if (cut<0)cut=line.IndexOf('：');
                        if (cut>=0 && cut<line.Length-1)candidate=line[(cut+1)..].Trim();
                        if (candidate.Length<2 && i+1<lines.Length)candidate=lines[i+1].Trim();
                    }
                    if (candidate.Length>220)candidate=candidate[..220];
                    if (string.IsNullOrWhiteSpace(candidate))continue;
                    result.Add(new Candidate {Value=candidate,Source=line,Page=page.PageNumber,Rank=matched?70:20});
                    if (result.Count>=400)break;
                }
                if (result.Count>=400)break;
            }
            var sorted=result.OrderByDescending(c=>c.Rank).ThenBy(c=>c.Page)
                .GroupBy(c=>c.Value,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).Take(150).ToList();
            if (string.IsNullOrEmpty(query) && _more.IsChecked!=true)sorted=sorted.Take(5).ToList();
            _changing=true;
            _candidates.ItemsSource=sorted;
            _candidates.DisplayMemberPath="Value";
            _candidates.SelectedItem=null;
            _changing=false;
            _evidence.Text=sorted.Count==0?"추천 후보가 없습니다. [추가 후보 보기] 또는 원본 PDF에서 확인해 주세요.":
                $"추천 후보 {sorted.Count}건. 후보를 선택하면 해당 원문과 페이지를 표시합니다.";
        }
        private static string MapField(string field) => field switch
        {
            "msds_product_name"=>"ProductNameBox", "msds_supplier_name"=>"SupplierNameBox",
            "msds_revision_date"=>"RevisionDateBox", "msds_revision_number"=>"RevisionNumberBox",
            "msds_supplier_phone"=>"PhoneBox", "msds_supplier_address"=>"AddressBox",
            "msds_un_number"=>"UnNumberBox", "msds_hazard_codes"=>"HazardCodesBox",
            "msds_autoignition"=>"AutoIgnitionBox", "msds_vapor_pressure"=>"VaporPressureBox",
            "msds_lel"=>"LelBox", "msds_uel"=>"UelBox", "msds_flash_point"=>"FlashPointBox",
            _=>""
        };
        private void ShowEvidence(int page,string source)
        {
            var pageText=_pdf.Pages.FirstOrDefault(p=>p.PageNumber==page)?.Text??"";
            _evidence.Text=$"페이지: {(page>0?page.ToString():"미확인")}\n\n선택한 원문:\n{source}\n\n페이지 전체 파싱 텍스트:\n{pageText}";
        }
        private void Apply(object sender,RoutedEventArgs e)
        {
            var changed=_rows.Where(r=>r.Value!=r.Original).ToList();
            if (changed.Count==0) { DialogResult=true;return; }
            if (WpfMessageBox.Show($"변경된 {changed.Count}개 항목을 등록 화면에 적용하시겠습니까?",
                "AI 교정 확인",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            try
            {
                SaveCorrections(changed);
                foreach(var row in changed)CorrectedValues[row.Key]=row.Value;
                DialogResult=true;
            }
            catch(Exception ex)
            {
                WpfMessageBox.Show("교정 이력 저장에 실패하여 적용하지 않았습니다.\n"+ex.Message,
                    "AI 교정 오류",MessageBoxButton.OK,MessageBoxImage.Error);
            }
        }
        private void SaveCorrections(List<FieldRow> changed)
        {
            using var conn=new SqliteConnection(DatabaseManager.GetConnectionString("msds"));
            conn.Open();
            using var transaction=conn.BeginTransaction();
            using(var create=conn.CreateCommand())
            {
                create.Transaction=transaction;
                create.CommandText=@"CREATE TABLE IF NOT EXISTS ai_correctionh_hist (
                    ai_correctionh_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ai_correctionh_pdf_path TEXT NOT NULL,
                    ai_correctionh_field_key TEXT NOT NULL,
                    ai_correctionh_original_value TEXT,
                    ai_correctionh_corrected_value TEXT,
                    ai_correctionh_corrected_at TEXT NOT NULL,
                    ai_correctionh_source TEXT NOT NULL
                )";
                create.ExecuteNonQuery();
            }
            foreach(var row in changed)
            {
                using var cmd=conn.CreateCommand();
                cmd.Transaction=transaction;
                cmd.CommandText=@"INSERT INTO ai_correctionh_hist
                    (ai_correctionh_pdf_path,ai_correctionh_field_key,ai_correctionh_original_value,
                     ai_correctionh_corrected_value,ai_correctionh_corrected_at,ai_correctionh_source)
                    VALUES(@path,@key,@old,@new,@at,@source)";
                cmd.Parameters.AddWithValue("@path",_filePath);
                cmd.Parameters.AddWithValue("@key",row.Key);
                cmd.Parameters.AddWithValue("@old",row.Original);
                cmd.Parameters.AddWithValue("@new",row.Value);
                cmd.Parameters.AddWithValue("@at",DateTimeOffset.Now.ToString("O"));
                cmd.Parameters.AddWithValue("@source","USER_CONFIRMED");
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }
}
