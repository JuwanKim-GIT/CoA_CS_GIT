using System;
using System.Windows;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace QCS
{
    internal sealed class FirstUserRegistrationWindow : Window
    {
        private readonly TextBox _name;
        private readonly TextBox _department;
        private readonly TextBox _email;
        private readonly TextBox _phone;
        internal QcsUser? RegisteredUser { get; private set; }

        internal FirstUserRegistrationWindow()
        {
            Title = "QCS 최초 사용자 등록";
            Width = 460;
            MinWidth = 420;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.SingleBorderWindow;
            Background = System.Windows.Media.Brushes.White;

            // Grid Auto 행으로 콘텐츠 높이에 맞춰 창을 확장하고 버튼 잘림을 방지한다.
            var root = new Grid { Margin = new Thickness(26, 22, 26, 24) };
            Content = root;
            for (int i = 0; i < 12; i++)
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            void AddRow(UIElement element, int row)
            {
                Grid.SetRow(element, row);
                root.Children.Add(element);
            }

            AddRow(new TextBlock
            {
                Text = "최초 사용자 등록",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 18)
            }, 0);
            AddRow(new TextBlock { Text = "Windows ID (변경 불가)" }, 1);
            AddRow(new TextBox
            {
                Text = Environment.UserName,
                IsReadOnly = true,
                Background = System.Windows.Media.Brushes.WhiteSmoke,
                Height = 34,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 5, 0, 14)
            }, 2);
            AddRow(new TextBlock { Text = "사용자 이름 (필수)" }, 3);
            _name = new TextBox
            {
                Height = 34,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 5, 0, 14)
            };
            AddRow(_name, 4);
            AddRow(new TextBlock { Text = "부서 (필수)" }, 5);
            _department = new TextBox
            {
                Height = 34,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 5, 0, 14)
            };
            AddRow(_department, 6);

            AddRow(new TextBlock { Text = "이메일 (선택)" }, 7);
            _email = new TextBox { Height = 34, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 5, 0, 14) };
            AddRow(_email, 8);
            AddRow(new TextBlock { Text = "전화번호 (선택)" }, 9);
            _phone = new TextBox { Height = 34, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 5, 0, 24) };
            AddRow(_phone, 10);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var cancel = new Button
            {
                Content = "취소", Width = 88, Height = 34,
                Margin = new Thickness(0, 0, 10, 0), IsCancel = true
            };
            cancel.Click += (_, _) => { DialogResult = false; };
            var save = new Button
            {
                Content = "등록", Width = 88, Height = 34, IsDefault = true
            };
            save.Click += (_, _) =>
            {
                try
                {
                    RegisteredUser = StartupUserService.Register(_name.Text, _department.Text, _email.Text, _phone.Text);
                    DialogResult = true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "사용자 등록 실패", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            AddRow(buttons, 11);
            Loaded += (_, _) => _name.Focus();
        }
    }
}
