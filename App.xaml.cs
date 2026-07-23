using System;
using System.Windows;

namespace CoA_CS
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // DB 초기화는 DatabaseManager로 이관 (네트워크 → 로컬 fallback)
            DatabaseManager.Initialize();
        }
    }
}
