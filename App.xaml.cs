using System;
using System.Linq;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace QCS
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                // 개별 DB 상태는 초기화 성공 여부와 별개로 확인한다.
                var dbStates = StartupUserService.CheckDatabases();
                // 기존 초기화(ATTACH, 인덱스 준비 등)는 그대로 실행한다.
                // 일부 DB가 실패한 경우에도 정상 DB를 사용하는 기능은 유지한다.
                try { DatabaseManager.Initialize(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("DB 초기화 경고: " + ex.Message); }

                if (dbStates["common"] != "정상")
                {
                    MessageBox.Show("사용자 정보를 저장하는 common DB에 연결할 수 없습니다.\n" + dbStates["common"],
                        "QCS 시작 오류", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(1); return;
                }

                QcsAuthorizationService.EnsureSecurityMetadata();

                QcsUser? user = StartupUserService.FindCurrentUser();
                if (user is null)
                {
                    var registration = new FirstUserRegistrationWindow();
                    if (registration.ShowDialog() != true) { Shutdown(); return; }
                    user = registration.RegisteredUser;
                }

                if (user == null || !QcsAuthorizationService.IsActive(user.UserLevel))
                {
                    MessageBox.Show("비활성 계정은 QCS 프로그램을 사용할 수 없습니다.\n시스템관리자 또는 IT 관리자에게 활성화를 요청해 주세요.",
                        "QCS 사용 제한", MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown(); return;
                }

                var main = new MainWindow();
                MainWindow = main;
                int connected = dbStates.Values.Count(v => v == "정상");
                string details = string.Join("\n", dbStates.Select(v => v.Key + ": " + v.Value));
                main.SetDatabaseStatus(connected, dbStates.Count, details);
                if (user != null) main.SetCurrentUser(user.WindowsId, user.Name, user.UserLevel);
                main.SetRunStatus(connected == dbStates.Count ? "준비 완료" : "일부 DB 연결 오류");
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                main.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show("프로그램 시작 중 오류가 발생했습니다.\n" + ex.Message,
                    "QCS 시작 오류", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }
    }
}
