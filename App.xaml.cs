using System;
using System.Windows;

namespace QCS
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // 네트워크 SQLite DB에 연결
            DatabaseManager.Initialize();
        }
    }
}
