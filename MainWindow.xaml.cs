// 🎯 [WPF 전용 매싱 완벽 잠금] 파일 다이얼로그 모호성 해결을 위한 별칭 선언
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Data.Sqlite;
using System.Data;

// 🎯 [WPF 전용 매싱 완벽 잠금] 윈폼과 충돌하는 알림창 및 버튼 자물쇠 추가
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using PasswordBox = System.Windows.Controls.PasswordBox;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;

namespace QCS
{
    public partial class MainWindow : Window
    {
        private QcsUser? _switchedUser;
        private static readonly Dictionary<string, string> ProgramIds = new(StringComparer.Ordinal)
        {
            ["CoA 발행"] = "CoARegistryView.xaml.cs", ["CoA 조회"] = "MainWindow.xaml.cs#Menu_CoaInq_Click",
            ["아이템 목록 관리"] = "ItemListView.xaml.cs", ["Test Result 목록 관리"] = "TestResultListView.xaml.cs",
            ["AI 교정학습 관리"] = "MsdsLearningManagementView.xaml.cs",
            ["MSDS 등록"] = "MsdsRegistrationView.xaml.cs", ["MSDS 조회/관리"] = "MsdsManagementView.xaml.cs",
            ["MSDS 대장출력"] = "MsdsInventoryExportView.xaml.cs", ["대장 개정관리"] = "AuditHistoryView.xaml.cs",
            ["참조자료 관리"] = "ReferenceDataView.xaml.cs", ["공통코드 관리"] = "CodeListView.xaml.cs", ["업무코드 관리"] = "UserCodeListView.xaml.cs",
            ["DB 스키마 조회"] = "DatabaseSchemaView.xaml.cs", ["Data Import"] = "MainWindow.xaml.cs#MenuDataImport_Click",
            ["Data Export"] = "MainWindow.xaml.cs#MenuDataExport_Click"
        };
        public MainWindow()
        {
            InitializeComponent();
            Closed += (_, _) => { foreach (var tab in MainTabControl.Items.OfType<System.Windows.Controls.TabItem>()) if (tab.Content is MsdsLearningManagementView learning) learning.CloseDiagnostic(); };
            // Windows identity can be shown without a database connection.
            // Name, department and activation state will be loaded from QCS_USERS
            // after DatabaseManager integration is available.
            CurrentUserText.Text = $"{Environment.UserName}(Windows ID)";
            UserStatusIcon.ToolTip = "사용자 활성화 상태 확인 전";
            Loaded += (_, _) =>
            {
                try { RefreshCurrentUserStatus(); }
                catch { /* Startup DB error is handled by existing initialization flow. */ }
            };

        }

        // Public UI update hooks for the future DB startup/user registration flow.
        // Database connection checks are intentionally NOT simulated here.
        public void SetRunStatus(string message)
        {
            RunStatusText.Text = message ?? string.Empty;
        }

        public void SetDatabaseStatus(int connected, int total, string? details = null)
        {
            if (total <= 0 || connected < 0 || connected > total)
                throw new ArgumentOutOfRangeException(nameof(connected));

            DatabaseStatusText.Text = connected == total
                ? $"DB 연결: {connected}/{total} 정상"
                : connected == 0
                    ? $"DB 연결: {connected}/{total} 연결 오류"
                    : $"DB 연결: {connected}/{total} 일부 오류";
            DatabaseStatusIcon.Fill = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                    connected == total ? "#16A34A" : connected == 0 ? "#DC2626" : "#EAB308"));
            DatabaseStatusText.ToolTip = details ?? DatabaseStatusText.Text;
        }

        // Compatibility overload for existing startup calls. Always use the DB role.
        public void SetCurrentUser(string windowsId, string name, string department, bool isActive)
        {
            try
            {
                var current = _switchedUser ?? StartupUserService.FindCurrentUser();
                SetCurrentUser(current?.WindowsId ?? windowsId, current?.Name ?? name, current?.UserLevel ?? 0);
            }
            catch
            {
                SetCurrentUser(windowsId, name, 0);
            }
        }

        public void SetCurrentUser(string windowsId, string name, int userLevel)
        {
            string role = userLevel == 9 ? "IT 관리자" : userLevel == 8 ? "시스템관리자" : userLevel == 1 ? "일반사용자" : "비활성";
            CurrentUserText.Text = $"{name}({windowsId}, {role})";
            UserStatusIcon.Fill = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                    userLevel == 0 ? "#94A3B8" : "#16A34A"));
            UserStatusIcon.ToolTip = role;
        }

        internal QcsUser? GetCurrentUser() => _switchedUser ?? StartupUserService.FindCurrentUser();

        public void RefreshCurrentUserStatus()
        {
            var current = GetCurrentUser();
            if (current != null) SetCurrentUser(current.WindowsId, current.Name, current.UserLevel);
        }

        private bool RequireProgramAccess(string tabHeader)
        {
            if (!ProgramIds.TryGetValue(tabHeader, out var programId)) return true;
            var current = GetCurrentUser();
            if (QcsAuthorizationService.CanAccessProgram(current, programId)) return true;
            SetRunStatus($"접근 제한: {tabHeader} 프로그램 사용권한이 없습니다.");
            MessageBox.Show("해당 프로그램의 사용권한이 없습니다.", "권한 안내", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        // ===================================================================
        // 📥 데이터 관리 - Import / Export 실구현 영역
        // ===================================================================

        private void MenuDataImport_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireProgramAccess("Data Import")) return;
            string currentFolder = AppDomain.CurrentDomain.BaseDirectory;

            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                InitialDirectory = currentFolder,
                Filter = "데이터 파일 (*.csv;*.xlsx;*.xls)|*.csv;*.xlsx;*.xls|CSV 파일 (*.csv)|*.csv|Excel 파일 (*.xlsx;*.xls)|*.xlsx;*.xls|모든 파일 (*.*)|*.*",
                Title = "데이터 임포트 파일 선택"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    string ext = Path.GetExtension(openFileDialog.FileName).ToLower();
                    if (ext == ".xlsx" || ext == ".xls")
                        DatabaseManager.ImportExcelToSqlite(openFileDialog.FileName);
                    else
                        DatabaseManager.ImportCsvToSqlite(openFileDialog.FileName);

                    MessageBox.Show("데이터 임포트가 성공적으로 완료되었습니다.", "완료", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (InvalidOperationException)
                {
                    // DetectTableFromHeaders에서 이미 MessageBox 표시 후 throw 했으므로 추가 처리 불필요
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"임포트 중 오류 발생:\n{ex.Message}", "에러", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void MenuDataExport_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireProgramAccess("Data Export")) return;
            MessageBox.Show("Data Export 기능은 현재 준비 중입니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ===================================================================
        // 🧱 XAML 연결용 실구현 영역 (탭 동적 생성 및 연동)
        // ===================================================================

        private void AddOrSelectTab(string tabHeader, UIElement? customContent = null)
        {
            if (!RequireProgramAccess(tabHeader)) return;
            foreach (TabItem item in MainTabControl.Items)
            {
                if (item.Tag != null && item.Tag.ToString() == tabHeader)
                {
                    MainTabControl.SelectedItem = item;
                    return;
                }
            }

            TabItem newTab = new TabItem
            {
                Tag = tabHeader,
                Padding = new Thickness(10, 5, 10, 5)
            };

            Grid headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });

            TextBlock txtTitle = new TextBlock
            {
                Text = tabHeader,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(txtTitle, 0);
            headerGrid.Children.Add(txtTitle);

            Button btnClose = new Button
            {
                Content = "×",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Width = 16,
                Height = 16,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = System.Windows.Media.Brushes.DimGray
            };

            btnClose.MouseEnter += (s, e) => btnClose.Foreground = System.Windows.Media.Brushes.Red;
            btnClose.MouseLeave += (s, e) => btnClose.Foreground = System.Windows.Media.Brushes.DimGray;

            btnClose.Click += (s, e) =>
            {
                e.Handled = true;
                if (newTab.Content is UserCodeListView view && !view.CanDiscardChanges()) return;
                if (newTab.Content is MsdsLearningManagementView learning) learning.CloseDiagnostic();
                MainTabControl.Items.Remove(newTab);
            };

            Grid.SetColumn(btnClose, 1);
            headerGrid.Children.Add(btnClose);
            newTab.Header = headerGrid;

            newTab.Content = customContent ?? new TextBlock
            {
                Text = $"{tabHeader} 화면이 준비 중입니다.",
                Margin = new Thickness(20),
                FontSize = 16,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };

            MainTabControl.Items.Add(newTab);
            MainTabControl.SelectedItem = newTab;
        }

        private void Menu_Item_Click(object sender, RoutedEventArgs e)
        {
            ItemListView itemListView = new ItemListView();
            AddOrSelectTab("아이템 목록 관리", itemListView);
        }

        private void Menu_Test_Click(object sender, RoutedEventArgs e)
        {
            TestResultListView testListView = new TestResultListView();
            AddOrSelectTab("Test Result 목록 관리", testListView);
        }

        private void Menu_UserCode_Click(object sender, RoutedEventArgs e)
        {
            if (RequireProgramAccess("업무코드 관리")) AddOrSelectTab("업무코드 관리", new UserCodeListView());
        }

        private void Menu_Code_Click(object sender, RoutedEventArgs e)
        {
            if (GetCurrentUser()?.UserLevel != 9) { SetRunStatus("공통코드 관리: Admin 전용입니다."); return; }
            CodeListView codeListView = new CodeListView();
            AddOrSelectTab("공통코드 관리", codeListView);
        }

        private void Menu_CoaReg_Click(object sender, RoutedEventArgs e)
        {
            CoARegistryView registryView = new CoARegistryView();
            AddOrSelectTab("CoA 발행", registryView);
        }

        private void Menu_CoaInq_Click(object sender, RoutedEventArgs e)
        {
            AddOrSelectTab("CoA 조회");
        }

        // =====================================================
        // MSDS 메뉴
        // =====================================================

        private void Menu_MsdsLearning_Click(object sender, RoutedEventArgs e)
        {
            AddOrSelectTab("AI 교정학습 관리", new MsdsLearningManagementView());
        }

        private void Menu_MsdsReg_Click(object sender, RoutedEventArgs e)
        {
            AddOrSelectTab("MSDS 등록", new MsdsRegistrationView());
        }

        private void Menu_MsdsManage_Click(object sender, RoutedEventArgs e)
        {
            AddOrSelectTab("MSDS 조회/관리", new MsdsManagementView());
        }

        private void Menu_RmInventory_Click(object sender, RoutedEventArgs e)
        {
            AddOrSelectTab("MSDS 대장출력", new MsdsInventoryExportView());
        }

        private void Menu_AuditHistory_Click(object sender, RoutedEventArgs e)
        {
            AddOrSelectTab("대장 개정관리", new AuditHistoryView());
        }

        private void Menu_MsdsReference_Click(object sender, RoutedEventArgs e)
        {
            AddOrSelectTab("참조자료 관리", new ReferenceDataView());
        }


        // =====================================================
        // 시스템 메뉴
        // =====================================================

        private void Menu_UserManage_Click(object sender, RoutedEventArgs e)
        {
            var current = GetCurrentUser();
            if (current == null || !QcsAuthorizationService.CanManageUsers(current.UserLevel))
            {
                SetRunStatus("접근 제한: 사용자 관리는 시스템관리자 이상만 사용할 수 있습니다.");
                MessageBox.Show("사용자 관리는 시스템관리자 이상만 사용할 수 있습니다.", "권한 안내", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            AddOrSelectTab("사용자 관리", new UserManagementView());
        }

        private void Menu_AccessManage_Click(object sender, RoutedEventArgs e)
        {
            var current = GetCurrentUser();
            if (current == null || !QcsAuthorizationService.CanManageProgramPermissions(current.UserLevel))
            {
                SetRunStatus("접근 제한: 프로그램 권한 관리는 시스템관리자 이상만 사용할 수 있습니다.");
                MessageBox.Show("프로그램 권한 관리는 시스템관리자 이상만 사용할 수 있습니다.", "권한 안내", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            AddOrSelectTab("프로그램 접근 권한 관리", new AccessPermissionView());
        }

        private void Menu_AdminManage_Click(object sender, RoutedEventArgs e)
        {
            var current = GetCurrentUser();
            if (current?.UserLevel != QcsAuthorizationService.LevelAdmin)
            {
                SetRunStatus("접근 제한: Admin 관리는 IT 관리자 전용입니다.");
                MessageBox.Show("Admin 관리는 IT 관리자만 사용할 수 있습니다.", "권한 안내", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            AddOrSelectTab("Admin 관리", new AdminManagementView());
        }

        private void Menu_SwitchUser_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Window
            {
                Title = "사용자 전환", Width = 410, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = System.Windows.Media.Brushes.White,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Malgun Gothic")
            };
            var panel = new StackPanel { Margin = new Thickness(24) };
            dialog.Content = panel;
            panel.Children.Add(new TextBlock { Text = "사용자 ID", Margin = new Thickness(0, 0, 0, 6) });
            var idInput = new TextBox { Height = 34, Padding = new Thickness(8, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center };
            panel.Children.Add(idInput);
            panel.Children.Add(new TextBlock { Text = "비밀번호", Margin = new Thickness(0, 16, 0, 6) });
            var passwordInput = new PasswordBox { Height = 34, Padding = new Thickness(8, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center };
            panel.Children.Add(passwordInput);
            var hint = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
            panel.Children.Add(hint);
            var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 22, 0, 0) };
            var cancel = new Button { Content = "취소", Width = 85, Height = 34, IsCancel = true };
            var confirm = new Button { Content = "전환", Width = 85, Height = 34, IsDefault = true,
                Margin = new Thickness(8, 0, 0, 0) };
            if (System.Windows.Application.Current.TryFindResource("QcsActionButton") is Style actionStyle)
                cancel.Style = actionStyle;
            if (System.Windows.Application.Current.TryFindResource("QcsPrimaryButton") is Style primaryStyle)
                confirm.Style = primaryStyle;
            cancel.Click += (_, _) => dialog.DialogResult = false;
            QcsUser? authenticated = null;
            confirm.Click += (_, _) =>
            {
                try
                {
                    authenticated = StartupUserService.AuthenticateSwitch(idInput.Text, passwordInput.Password);
                    if (authenticated == null)
                    {
                        hint.Text = "사용자 ID 또는 비밀번호가 올바르지 않습니다.";
                        passwordInput.Clear();
                        passwordInput.Focus();
                        SetRunStatus("사용자 전환 실패: 인증 정보가 일치하지 않습니다.");
                        return;
                    }
                    dialog.DialogResult = true;
                }
                catch (Exception ex)
                {
                    hint.Text = "인증 처리 중 오류가 발생했습니다.";
                    SetRunStatus("사용자 전환 실패: DB 인증 오류");
                    MessageBox.Show(dialog, ex.Message, "사용자 인증 오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(confirm);
            panel.Children.Add(buttons);
            dialog.Loaded += (_, _) => idInput.Focus();
            if (QcsModal.Show(dialog, this) == true && authenticated != null)
            {
                foreach (var tab in MainTabControl.Items.OfType<TabItem>().ToList())
                    if (tab.Content is UserCodeListView view && !view.CanDiscardChanges()) { SetRunStatus("사용자 전환 취소"); return; }
                foreach (var tab in MainTabControl.Items.OfType<TabItem>().ToList())
                    if (tab.Content is UserCodeListView) MainTabControl.Items.Remove(tab);
                _switchedUser = authenticated;
                RefreshCurrentUserStatus();
                SetRunStatus($"사용자 전환 완료: {authenticated.Name} ({authenticated.WindowsId})");
            }
            else
            {
                SetRunStatus("사용자 전환 취소: 기존 사용자를 유지합니다.");
            }
        }

        private void Menu_DatabaseSchema_Click(object sender, RoutedEventArgs e)
        {
            AddOrSelectTab("DB 스키마 조회", new DatabaseSchemaView());
        }
    }
}