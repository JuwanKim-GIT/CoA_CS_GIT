using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Data.Sqlite;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace QCS
{
    public partial class UserManagementView : UserControl
    {
        private sealed class UserRow
        {
            public string WindowsId { get; set; } = "";
            public string Name { get; set; } = "";
            public string Department { get; set; } = "";
            public string Email { get; set; } = "";
            public string Phone { get; set; } = "";
            public int Level { get; set; }
            public string StatusText => Level == 9 ? "관리자 (9)" : Level == 1 ? "일반사용자 (1)" : "비활성 (0)";
            public bool Matches(string term) => new[] { WindowsId, Name, Department, Email, Phone }
                .Any(x => x.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        private List<UserRow> _all = new();
        private int _level;
        private string _id = "";

        public UserManagementView()
        {
            InitializeComponent();
            Loaded += (_, _) => LoadUsers();
        }

        private static SqliteConnection OpenCommon()
        {
            var conn = new SqliteConnection(DatabaseManager.GetConnectionString("common"));
            conn.Open();
            return conn;
        }

        private bool RefreshIdentity()
        {
            var user = StartupUserService.FindCurrentUser();
            _id = Environment.UserName;
            _level = user?.UserLevel ?? 0;
            bool admin = _level == 9;
            SearchText.IsEnabled = admin;
            SearchButton.IsEnabled = admin;
            StatusButton.IsEnabled = admin;
            PromoteButton.IsEnabled = admin;
            EditButton.IsEnabled = _level != 0;
            return _level != 0;
        }

        private void LoadUsers()
        {
            try
            {
                if (!RefreshIdentity())
                {
                    _all.Clear(); UsersGrid.ItemsSource = null;
                    TotalCount.Text = ActiveCount.Text = InactiveCount.Text = "0 명";
                    StatusText.Text = "비활성 계정: 사용자 관리 기능을 사용할 수 없습니다.";
                    return;
                }
                var rows = new List<UserRow>();
                using var conn = OpenCommon();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT zx_code_value, zx_code_desc1, zx_code_desc2, zx_code_char1, zx_code_char2, zx_code_cmmt FROM zx_code_mstr WHERE zx_code_fldname='QCS_USERS'" +
                    (_level == 9 ? "" : " AND zx_code_value=$id COLLATE NOCASE") + " ORDER BY zx_code_value";
                if (_level != 9) cmd.Parameters.AddWithValue("$id", _id);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string Get(int i) => reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString() ?? "";
                    rows.Add(new UserRow { WindowsId = Get(0), Name = Get(1), Department = Get(2),
                        Email = Get(3), Phone = Get(4), Level = StartupUserService.ParseLevel(Get(5)) });
                }
                _all = rows;
                TotalCount.Text = $"{rows.Count} 명";
                ActiveCount.Text = $"{rows.Count(x => x.Level != 0)} 명";
                InactiveCount.Text = $"{rows.Count(x => x.Level == 0)} 명";
                ApplyFilter();
            }
            catch (Exception ex) { StatusText.Text = "조회 실패"; MessageBox.Show(ex.Message, "사용자 조회 오류", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        // Recursive descent: AND has higher precedence than OR; adjacent terms imply AND.
        private sealed class SearchParser
        {
            private readonly List<string> _tokens;
            private int _pos;
            private readonly UserRow _row;
            public SearchParser(string query, UserRow row)
            {
                _tokens = Regex.Matches(query, @"\(|\)|\&|\||[^\s()&|]+")
                    .Select(m => m.Value).ToList();
                _row = row;
            }
            private string? Peek() => _pos < _tokens.Count ? _tokens[_pos] : null;
            private bool Is(string word) => string.Equals(Peek(), word, StringComparison.OrdinalIgnoreCase);
            private bool Take(string word) { if (!Is(word)) return false; _pos++; return true; }
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
                {
                    Take("AND"); Take("&");
                    bool right = ParseAtom(); result = result && right;
                }
                return result;
            }
            private bool ParseAtom()
            {
                if (Take("("))
                {
                    bool result = ParseOr();
                    if (!Take(")")) throw new FormatException("닫는 괄호 ')'가 없습니다.");
                    return result;
                }
                var term = Peek();
                if (term == null || Is(")") || Is("AND") || Is("OR") || Is("&") || Is("|"))
                    throw new FormatException("검색어 또는 괄호가 필요합니다.");
                _pos++;
                return _row.Matches(term);
            }
        }

        private void ApplyFilter()
        {
            try
            {
                string query = _level == 9 ? SearchText.Text.Trim() : "";
                // Validate syntax even if the result set is empty.
                _ = new SearchParser(query, new UserRow()).Evaluate();
                var result = _all.Where(x => new SearchParser(query, x).Evaluate()).ToList();
                UsersGrid.ItemsSource = result;
                UpdateSelectionUi();
            }
            catch (FormatException ex) { StatusText.Text = "검색식 오류: " + ex.Message; MessageBox.Show(ex.Message, "검색식 오류", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        private void SearchText_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (SearchHint != null) SearchHint.Visibility = string.IsNullOrEmpty(SearchText.Text) ? Visibility.Visible : Visibility.Collapsed;
        }
        private void Search_Click(object sender, RoutedEventArgs e) { if (_level == 9) ApplyFilter(); }
        private void Search_KeyDown(object sender, KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Enter && _level == 9) ApplyFilter(); }
        private void PublishStatus(string message)
        {
            if (Window.GetWindow(this) is MainWindow main)
                main.SetRunStatus(message);
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            LoadUsers();
            PublishStatus("사용자 목록 새로고침 완료");
        }
        private UserRow? Selected() => UsersGrid.SelectedItem as UserRow;
        private List<UserRow> SelectedRows() => UsersGrid.SelectedItems.Cast<UserRow>().ToList();
        private void UsersGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateSelectionUi();
        private void UpdateSelectionUi()
        {
            if (UsersGrid == null || StatusButton == null || StatusText == null || EditButton == null || PromoteButton == null) return;
            var selected = SelectedRows();
            EditButton.IsEnabled = _level != 0 && selected.Count == 1;
            StatusButton.IsEnabled = _level == 9 && selected.Count > 0;
            PromoteButton.IsEnabled = _level == 9 && selected.Count > 0;
            StatusButton.Content = selected.Count == 1 ? (selected[0].Level == 0 ? "활성화" : "비활성화") : "활성화/비활성화";
            int shown = UsersGrid.ItemsSource is System.Collections.ICollection items ? items.Count : 0;
            StatusText.Text = $"조회 {shown}건 / 전체 {_all.Count}건 / 선택 {selected.Count}건";
        }


        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRows().Count != 1) { PublishStatus("사용자 정보 수정 취소: 한 명을 선택해 주세요."); MessageBox.Show("수정할 사용자 한 명을 선택해 주세요."); return; }
            var row = Selected()!;
            try
            {
                if (!RefreshIdentity() || (_level != 9 && !string.Equals(_id, row.WindowsId, StringComparison.OrdinalIgnoreCase)))
                {
                    PublishStatus("사용자 정보 수정 제한: 권한이 없습니다.");
                    MessageBox.Show("다른 사용자의 정보는 수정할 수 없습니다.", "권한 안내", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            catch (Exception ex) { PublishStatus("사용자 정보 수정 실패"); MessageBox.Show(ex.Message, "권한 오류"); return; }
            var dialog = new Window { Title = "사용자 정보 수정 - " + row.WindowsId, Width = 460,
                SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this),
                Background = System.Windows.Media.Brushes.White };
            var panel = new StackPanel { Margin = new Thickness(24) }; dialog.Content = panel;
            dialog.FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Malgun Gothic");
            TextBox AddField(string label, string value)
            {
                panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 5) });
                var tb = new TextBox { Text = value, Height = 32, VerticalContentAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(8, 0, 8, 0), Style = (Style)System.Windows.Application.Current.FindResource("QcsInput") };
                panel.Children.Add(tb); return tb;
            }
            panel.Children.Add(new TextBlock { Text = "Windows ID: " + row.WindowsId, FontWeight = FontWeights.SemiBold });
            var name = AddField("사용자 이름 (필수)", row.Name);
            var dept = AddField("부서 (필수)", row.Department);
            var email = AddField("이메일 (선택)", row.Email);
            var phone = AddField("전화번호 (선택)", row.Phone);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0) };
            var cancel = new Button { Content = "취소", Width = 82, Height = 32, IsCancel = true, Style = (Style)System.Windows.Application.Current.FindResource("QcsActionButton") };
            var save = new Button { Content = "저장", Width = 82, Height = 32, IsDefault = true, Margin = new Thickness(8, 0, 0, 0), Style = (Style)System.Windows.Application.Current.FindResource("QcsPrimaryButton") };
            cancel.Click += (_, _) => dialog.DialogResult = false;
            save.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(dept.Text))
                { PublishStatus("사용자 정보 수정 제한: 이름과 부서는 필수입니다."); MessageBox.Show(dialog, "이름과 부서는 필수입니다."); return; }
                try
                {
                    if (!RefreshIdentity())
                    {
                        PublishStatus("사용자 정보 수정 제한: 비활성 계정입니다.");
                        MessageBox.Show(dialog, "비활성 계정은 수정할 수 없습니다.", "권한 안내", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    using var conn = OpenCommon(); using var cmd = conn.CreateCommand();
                    cmd.CommandText = "UPDATE zx_code_mstr SET zx_code_desc1=$name, zx_code_desc2=$dept, zx_code_char1=$email, zx_code_char2=$phone WHERE zx_code_fldname='QCS_USERS' AND zx_code_value=$id COLLATE NOCASE" +
                        (_level == 9 ? "" : " AND zx_code_value=$actor COLLATE NOCASE AND zx_code_cmmt='1'");
                    cmd.Parameters.AddWithValue("$name", name.Text.Trim()); cmd.Parameters.AddWithValue("$dept", dept.Text.Trim());
                    cmd.Parameters.AddWithValue("$email", email.Text.Trim()); cmd.Parameters.AddWithValue("$phone", phone.Text.Trim());
                    cmd.Parameters.AddWithValue("$id", row.WindowsId);
                    if (_level != 9) cmd.Parameters.AddWithValue("$actor", _id);
                    if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("수정 대상이 없거나 수정 권한이 없습니다.");
                    dialog.DialogResult = true;
                }
                catch (Exception ex) { PublishStatus("사용자 정보 수정 실패"); MessageBox.Show(dialog, ex.Message, "수정 오류", MessageBoxButton.OK, MessageBoxImage.Error); }
            };
            buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
            if (QcsModal.Show(dialog, Window.GetWindow(this)) == true)
            {
                LoadUsers();
                if (Window.GetWindow(this) is MainWindow main) main.RefreshCurrentUserStatus();
                PublishStatus($"사용자 {row.WindowsId} 정보 수정 완료");
            }
            else
            {
                PublishStatus($"사용자 {row.WindowsId} 정보 수정 취소");
            }
        }

        private void Status_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedRows();
            if (selected.Count == 0) return;
            if (selected.Count == 1)
            {
                int target = selected[0].Level == 0 ? 1 : 0;
                ChangeStatus(selected, target, false);
                return;
            }
            var dlg = new QcsBatchChoiceDialog(selected.Count, selected.Count(x => x.Level != 0), selected.Count(x => x.Level == 0));
            if (QcsModal.Show(dlg, Window.GetWindow(this)) == true && dlg.Choice.HasValue)
                ChangeStatus(selected, dlg.Choice.Value, true);
            else
                PublishStatus("사용자 일괄 상태 변경 취소");
        }

        private void Promote_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedRows();
            if (selected.Count == 0) return;
            ChangeStatus(selected, 9, selected.Count > 1);
        }

        private void ChangeStatus(List<UserRow> selected, int targetLevel, bool batch)
        {
            try
            {
                if (!RefreshIdentity() || _level != 9)
                {
                    PublishStatus("사용자 상태 변경 제한: 관리자 권한이 필요합니다.");
                    MessageBox.Show("관리자만 사용자 상태를 변경할 수 있습니다.", "권한 안내", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (targetLevel == 0 && selected.Any(x => string.Equals(x.WindowsId, _id, StringComparison.OrdinalIgnoreCase)))
                {
                    PublishStatus("작업 제한: 현재 로그인한 관리자는 비활성화할 수 없습니다.");
                    MessageBox.Show("현재 로그인한 관리자 본인은 비활성화할 수 없습니다.", "상태 변경 안내", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                string action = targetLevel == 9 ? "관리자로 지정" : targetLevel == 1 ? "활성화" : "비활성화";
                if (!batch && MessageBox.Show($"{selected[0].Name} ({selected[0].WindowsId}) 사용자를 {action}하시겠습니까?", "상태 변경 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    PublishStatus($"사용자 {selected[0].WindowsId} {action} 취소");
                    return;
                }
                if (targetLevel == 9 && batch && MessageBox.Show($"선택한 {selected.Count}명에게 관리자 권한을 부여하시겠습니까?", "관리자 일괄 지정", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    PublishStatus("관리자 일괄 지정 취소");
                    return;
                }
                using var conn = OpenCommon();
                using var tx = conn.BeginTransaction();
                // Guard inside the transaction, before any updates are committed.
                if (targetLevel == 0)
                {
                    using var adminCheck = conn.CreateCommand();
                    adminCheck.Transaction = tx;
                    adminCheck.CommandText = "SELECT zx_code_value FROM zx_code_mstr WHERE zx_code_fldname='QCS_USERS' AND zx_code_cmmt='9'";
                    using var adminReader = adminCheck.ExecuteReader();
                    var admins = new List<string>();
                    while (adminReader.Read()) admins.Add(adminReader.GetString(0));
                    if (admins.Count > 0 && admins.All(a => selected.Any(r => string.Equals(r.WindowsId, a, StringComparison.OrdinalIgnoreCase))))
                    {
                        PublishStatus("작업 제한: 최소 1명의 활성 관리자가 남아 있어야 합니다.");
                        MessageBox.Show("최소 1명의 활성 관리자가 남아 있어야 합니다.", "상태 변경 안내", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                }
                using var cmd = conn.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = "UPDATE zx_code_mstr SET zx_code_cmmt=$state WHERE zx_code_fldname='QCS_USERS' AND zx_code_value=$id COLLATE NOCASE AND EXISTS (SELECT 1 FROM zx_code_mstr WHERE zx_code_fldname='QCS_USERS' AND zx_code_value=$actor COLLATE NOCASE AND zx_code_cmmt='9')";
                var state = cmd.Parameters.Add("$state", SqliteType.Text);
                var id = cmd.Parameters.Add("$id", SqliteType.Text);
                cmd.Parameters.AddWithValue("$actor", _id);
                foreach (var row in selected)
                {
                    // Activating an administrator must not demote them.
                    if (targetLevel == 1 && row.Level == 9) continue;
                    if (targetLevel == 9 && row.Level == 9) continue;
                    if (targetLevel == 0 && row.Level == 0) continue;
                    state.Value = targetLevel.ToString(); id.Value = row.WindowsId;
                    if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException($"{row.WindowsId}: 변경 대상이 없거나 권한이 없습니다.");
                }
                using (var check = conn.CreateCommand())
                {
                    check.Transaction = tx;
                    check.CommandText = "SELECT COUNT(*) FROM zx_code_mstr WHERE zx_code_fldname='QCS_USERS' AND zx_code_cmmt='9'";
                    if (Convert.ToInt32(check.ExecuteScalar()) < 1)
                        throw new InvalidOperationException("최소 1명의 활성 관리자가 남아 있어야 합니다.");
                }
                tx.Commit();
                LoadUsers();
                if (Window.GetWindow(this) is MainWindow main) main.RefreshCurrentUserStatus();
                PublishStatus(selected.Count == 1
                    ? $"사용자 {selected[0].WindowsId} {action} 완료"
                    : $"사용자 {selected.Count}명 {action} 완료");
            }
            catch (Exception ex) { PublishStatus("사용자 상태 변경 실패"); MessageBox.Show(ex.Message, "상태 변경 오류", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }
}
