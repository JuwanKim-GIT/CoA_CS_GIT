using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace QCS
{
    internal sealed class QcsBatchChoiceDialog : Window
    {
        public int? Choice { get; private set; }
        public QcsBatchChoiceDialog(int count, int active, int inactive)
        {
            Title = "사용자 상태 일괄 변경"; Width = 460; SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = System.Windows.Media.Brushes.White; FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Malgun Gothic");
            var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
            panel.Children.Add(new TextBlock { Text = $"선택한 {count}명의 상태를 일괄 변경하시겠습니까?", FontSize = 16, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = $"활성 {active}명 / 비활성 {inactive}명", Margin = new Thickness(0, 12, 0, 18), Foreground = System.Windows.Media.Brushes.DimGray });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            void Add(string title, int? choice, string style)
            {
                var b = new Button { Content = title, Margin = new Thickness(5, 0, 0, 0), MinWidth = 100, Style = (Style)System.Windows.Application.Current.FindResource(style) };
                b.Click += (_, _) => { Choice = choice; DialogResult = choice != null; };
                buttons.Children.Add(b);
            }
            Add("모두 활성화", 1, "QcsPrimaryButton"); Add("모두 비활성화", 0, "QcsDangerButton"); Add("취소", null, "QcsActionButton");
            panel.Children.Add(buttons);
        }
    }
}
