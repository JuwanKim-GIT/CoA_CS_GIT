using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace QCS
{
    public partial class AccessPermissionView : UserControl
    {
        private sealed class ProgramRow
        {
            public string Category { get; set; } = "";
            public string MenuName { get; set; } = "";
            public string FileName { get; set; } = "";
            public string Description { get; set; } = "";
            public string Permission { get; set; } = "";
            public string AdminOnly { get; set; } = "N";
            public string ProgramType { get; set; } = "MENU";
            public string AdminOnlyText => string.Equals(AdminOnly, "Y", StringComparison.OrdinalIgnoreCase) ? "Y" : "N";
            public string DisplayPath => ProgramType == "SUB" ? "서브 프로그램  >  " + MenuName : Category + "  >  " + MenuName;
        }
        private sealed class UserRow
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public int Level { get; set; }
            public string LevelText => Level == 9 ? "IT 관리자" : Level == 8 ? "시스템관리자" : Level == 1 ? "일반사용자" : "비활성";
            public string Display => Id + "  (" + Name + ")";
        }

        private static readonly Dictionary<string, string> Files = new(StringComparer.Ordinal)
        {
            ["CoA 발행"] = "CoARegistryView.xaml.cs", ["CoA 조회"] = "MainWindow.xaml.cs#Menu_CoaInq_Click",
            ["아이템 목록 관리"] = "ItemListView.xaml.cs", ["Test Result 목록 관리"] = "TestResultListView.xaml.cs",
            ["MSDS 등록"] = "MsdsRegistrationView.xaml.cs", ["MSDS 조회/관리"] = "MsdsManagementView.xaml.cs",
            ["MSDS 대장출력"] = "MsdsInventoryExportView.xaml.cs", ["대장 개정관리"] = "AuditHistoryView.xaml.cs",
            ["참조자료 관리"] = "ReferenceDataView.xaml.cs", ["공통코드 관리"] = "CodeListView.xaml.cs", ["업무코드 관리"] = "UserCodeListView.xaml.cs",
            ["사용자 관리"] = "UserManagementView.xaml.cs", ["프로그램 접근 권한 관리"] = "AccessPermissionView.xaml.cs",
            ["Admin 관리"] = "AdminManagementView.xaml.cs", ["DB 스키마 조회"] = "DatabaseSchemaView.xaml.cs",
            ["Data Import"] = "MainWindow.xaml.cs#MenuDataImport_Click", ["Data Export"] = "MainWindow.xaml.cs#MenuDataExport_Click",
            ["사용자 전환"] = "UserSwitchView.xaml.cs"
        };

        private readonly ObservableCollection<ProgramRow> _programs = new();
        private readonly ObservableCollection<UserRow> _allowed = new();
        private readonly List<UserRow> _users = new();
        private ProgramRow? _selected;
        private bool _loading;
        private QcsUser? _actor;
        private bool IsAdmin => _actor?.UserLevel == QcsAuthorizationService.LevelAdmin;
        private bool CanManage => _actor != null && QcsAuthorizationService.CanManageProgramPermissions(_actor.UserLevel);

        public AccessPermissionView()
        {
            InitializeComponent();
            ProgramsGrid.ItemsSource = _programs;
            AllowedGrid.ItemsSource = _allowed;
            Loaded += (_, _) => InitializeView();
        }

        private void Report(string message)
        {
            StatusText.Text = message;
            if (Window.GetWindow(this) is MainWindow main) main.SetRunStatus(message);
        }

        private void InitializeView()
        {
            try
            {
                QcsAuthorizationService.EnsureSecurityMetadata();
                _actor = (Window.GetWindow(this) as MainWindow)?.GetCurrentUser() ?? StartupUserService.FindCurrentUser();
                SaveButton.IsEnabled = SyncButton.IsEnabled = CanManage;
                LoadUsers();
                SyncPrograms(CanManage);
                Report(CanManage ? "접근 권한 관리 준비 완료" : "조회 전용: 시스템관리자 이상만 권한을 변경할 수 있습니다.");
            }
            catch (Exception ex)
            {
                Report("권한 관리 조회 실패: " + ex.Message);
                MessageBox.Show(ex.Message, "접근 권한 관리", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static SqliteConnection OpenCommon()
        {
            var connection = new SqliteConnection(DatabaseManager.GetConnectionString("common"));
            connection.Open(); return connection;
        }

        private void LoadUsers()
        {
            _users.Clear();
            using var conn = OpenCommon();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT zx_code_value,zx_code_desc1,zx_code_cmmt FROM zx_code_mstr WHERE zx_code_fldname='QCS_USERS' ORDER BY zx_code_value";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string Get(int i) => reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString() ?? "";
                _users.Add(new UserRow { Id = Get(0), Name = Get(1), Level = StartupUserService.ParseLevel(Get(2)) });
            }
            UserCombo.ItemsSource = _users.Where(u => u.Level != 0 && (IsAdmin || u.Level != 9)).ToList();
        }

        private List<ProgramRow> DiscoverMenu()
        {
            var main = Window.GetWindow(this);
            if (main == null) throw new InvalidOperationException("메인 화면을 찾을 수 없습니다.");
            var menu = FindMenu(main);
            if (menu == null) throw new InvalidOperationException("메인 메뉴를 찾을 수 없습니다.");
            var result = new List<ProgramRow>();
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                string category = item.Header?.ToString() ?? "";
                foreach (var child in item.Items.OfType<MenuItem>())
                {
                    string name = child.Header?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    string file = child.Tag as string ?? (Files.TryGetValue(name, out var known) ? known : name + ".cs");
                    result.Add(new ProgramRow { Category = category, MenuName = name, FileName = file, Description = category + " > " + name, ProgramType = "MENU" });
                }
            }
            return result;
        }

        private static Menu? FindMenu(DependencyObject root)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is Menu menu) return menu;
                if (child is DependencyObject d)
                {
                    var found = FindMenu(d);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private void SyncPrograms(bool insertMissing)
        {
            var discovered = DiscoverMenu();
            var selectedFile = _selected?.FileName;
            using var conn = OpenCommon();
            if (insertMissing)
            {
                using var transaction = conn.BeginTransaction();
                foreach (var item in discovered)
                {
                    using var cmd = conn.CreateCommand(); cmd.Transaction = transaction;
                    cmd.CommandText = @"INSERT INTO zx_code_mstr (zx_code_fldname,zx_code_value,zx_code_desc1,zx_code_desc2,zx_code_cmmt,zx_code_char1,zx_code_char2)
SELECT 'QCS_APPLICATIONS',$file,$menu,$desc,'','N','MENU' WHERE NOT EXISTS
(SELECT 1 FROM zx_code_mstr WHERE zx_code_fldname='QCS_APPLICATIONS' AND zx_code_value=$file)";
                    cmd.Parameters.AddWithValue("$file", item.FileName);
                    cmd.Parameters.AddWithValue("$menu", item.MenuName);
                    cmd.Parameters.AddWithValue("$desc", item.Description);
                    cmd.ExecuteNonQuery();
                }
                transaction.Commit();
                QcsAuthorizationService.EnsureSecurityMetadata();
            }

            using var load = conn.CreateCommand();
            load.CommandText = "SELECT zx_code_value,zx_code_desc1,zx_code_desc2,zx_code_cmmt,zx_code_char1,zx_code_char2 FROM zx_code_mstr WHERE zx_code_fldname='QCS_APPLICATIONS' ORDER BY CASE WHEN COALESCE(zx_code_char2,'MENU')='SUB' THEN 1 ELSE 0 END,zx_code_desc2,zx_code_desc1";
            using var reader = load.ExecuteReader();
            var saved = new Dictionary<string, (string menu, string desc, string permission, string adminOnly, string type)>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                string Get(int i) => reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString() ?? "";
                saved[Get(0)] = (Get(1), Get(2), Get(3), Get(4), Get(5));
            }

            _loading = true;
            _programs.Clear();
            var menuLookup = discovered.ToDictionary(x => x.FileName, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in saved)
            {
                var row = pair.Value;
                if (menuLookup.TryGetValue(pair.Key, out var found))
                {
                    found.Permission = row.permission;
                    found.Description = string.IsNullOrWhiteSpace(row.desc) ? found.Description : row.desc;
                    found.AdminOnly = string.IsNullOrWhiteSpace(row.adminOnly) ? "N" : row.adminOnly;
                    found.ProgramType = string.IsNullOrWhiteSpace(row.type) ? "MENU" : row.type;
                    _programs.Add(found);
                }
                else
                {
                    _programs.Add(new ProgramRow
                    {
                        Category = row.type.Equals("SUB", StringComparison.OrdinalIgnoreCase) ? "서브 프로그램" : "등록 프로그램",
                        MenuName = row.menu,
                        FileName = pair.Key,
                        Description = row.desc,
                        Permission = row.permission,
                        AdminOnly = string.IsNullOrWhiteSpace(row.adminOnly) ? "N" : row.adminOnly,
                        ProgramType = string.IsNullOrWhiteSpace(row.type) ? "MENU" : row.type
                    });
                }
            }
            _loading = false;
            CountText.Text = $"프로그램: {_programs.Count}건";
            ProgramsGrid.SelectedItem = _programs.FirstOrDefault(x => x.FileName == selectedFile) ?? _programs.FirstOrDefault();
        }

        private void Programs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            _selected = ProgramsGrid.SelectedItem as ProgramRow;
            _loading = true;
            _allowed.Clear();
            MenuPathText.Text = "메뉴 경로: " + (_selected?.DisplayPath ?? "-");
            FileText.Text = "프로그램명: " + (_selected?.FileName ?? "-");
            DescriptionText.Text = "설명: " + (_selected?.Description ?? "-");
            TypeText.Text = "유형: " + (_selected?.ProgramType ?? "-");
            bool all = _selected?.Permission.Trim() == "*";
            AllowAllCheck.IsChecked = all;
            AdminOnlyCheck.IsChecked = string.Equals(_selected?.AdminOnly, "Y", StringComparison.OrdinalIgnoreCase);
            if (_selected != null && !all)
            {
                foreach (var id in _selected.Permission.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase))
                    _allowed.Add(_users.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? new UserRow { Id = id, Name = "(미등록 계정)" });
            }
            _loading = false;
            UpdateControls();
        }

        private void UpdateControls()
        {
            bool enabled = CanManage && _selected != null && AllowAllCheck.IsChecked != true;
            AllowAllCheck.IsEnabled = CanManage && _selected != null;
            AdminOnlyCheck.IsEnabled = IsAdmin && _selected != null;
            UserCombo.IsEnabled = AddButton.IsEnabled = AllowedGrid.IsEnabled = RemoveButton.IsEnabled = enabled;
            SaveButton.IsEnabled = CanManage && _selected != null;
        }

        private void AllowAll_Changed(object sender, RoutedEventArgs e) { if (!_loading) UpdateControls(); }
        private void Add_Click(object sender, RoutedEventArgs e)
        {
            if (!CanManage || AllowAllCheck.IsChecked == true || UserCombo.SelectedItem is not UserRow user) return;
            if (!IsAdmin && user.Level == 9) { Report("Admin 계정의 프로그램 권한은 SO가 변경할 수 없습니다."); return; }
            if (_allowed.Any(x => x.Id.Equals(user.Id, StringComparison.OrdinalIgnoreCase))) { Report("이미 추가된 사용자입니다."); return; }
            _allowed.Add(user);
        }
        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (!CanManage || AllowedGrid.SelectedItem is not UserRow user) return;
            if (!IsAdmin && user.Level == 9) { Report("Admin 계정의 프로그램 권한은 SO가 변경할 수 없습니다."); return; }
            _allowed.Remove(user);
        }
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Programs_SelectionChanged(this, new SelectionChangedEventArgs(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new List<object>(), new List<object>()));
            Report("권한 변경 취소");
        }
        private void Sync_Click(object sender, RoutedEventArgs e)
        {
            if (!CanManage) return;
            try { SyncPrograms(true); Report("프로그램 목록 동기화 완료"); }
            catch (Exception ex) { Report("동기화 실패: " + ex.Message); MessageBox.Show(ex.Message, "동기화 오류", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!CanManage || _selected == null || _actor == null) return;
            string permission = AllowAllCheck.IsChecked == true ? "*" : string.Join(",", _allowed.Select(x => x.Id));
            string adminOnly = AdminOnlyCheck.IsChecked == true ? "Y" : "N";
            try
            {
                using var conn = OpenCommon();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = IsAdmin
                    ? @"UPDATE zx_code_mstr SET zx_code_cmmt=$permission,zx_code_char1=$adminOnly
WHERE zx_code_fldname='QCS_APPLICATIONS' AND zx_code_value=$file
AND EXISTS (SELECT 1 FROM zx_code_mstr WHERE zx_code_fldname='QCS_USERS' AND zx_code_value=$actor COLLATE NOCASE AND zx_code_cmmt='9')"
                    : @"UPDATE zx_code_mstr SET zx_code_cmmt=$permission
WHERE zx_code_fldname='QCS_APPLICATIONS' AND zx_code_value=$file
AND EXISTS (SELECT 1 FROM zx_code_mstr WHERE zx_code_fldname='QCS_USERS' AND zx_code_value=$actor COLLATE NOCASE AND zx_code_cmmt='8')";
                cmd.Parameters.AddWithValue("$permission", permission);
                cmd.Parameters.AddWithValue("$adminOnly", adminOnly);
                cmd.Parameters.AddWithValue("$file", _selected.FileName);
                cmd.Parameters.AddWithValue("$actor", _actor.WindowsId);
                if (cmd.ExecuteNonQuery() != 1) { Report("권한 저장 제한: 현재 사용자 권한 또는 프로그램 등록 상태를 확인하세요."); return; }
                _selected.Permission = permission;
                if (IsAdmin) _selected.AdminOnly = adminOnly;
                SyncPrograms(false);
                Report($"{_selected?.MenuName}: 접근 권한 저장 완료");
            }
            catch (Exception ex) { Report("권한 저장 실패: " + ex.Message); MessageBox.Show(ex.Message, "권한 저장 오류", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }
}
