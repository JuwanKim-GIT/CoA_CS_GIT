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

namespace CoA_CS
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            // DB 상태 변경 이벤트 구독 및 초기 상태 반영
            DatabaseManager.StatusChanged += OnDbStatusChanged;
            OnDbStatusChanged(DatabaseManager.IsOnline);
        }

        // ===================================================================
        // 📥 데이터 관리 - Import / Export 실구현 영역
        // ===================================================================

        private void MenuDataImport_Click(object sender, RoutedEventArgs e)
        {
            string currentFolder = AppDomain.CurrentDomain.BaseDirectory;

            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                InitialDirectory = currentFolder,
                Filter = "CSV 파일 (*.csv)|*.csv|모든 파일 (*.*)|*.*",
                Title = "IBIS 덤프 CSV 파일 선택"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
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
            MessageBox.Show("Data Export 기능은 현재 준비 중입니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ===================================================================
        // 🧱 XAML 연결용 실구현 영역 (탭 동적 생성 및 연동)
        // ===================================================================

        private void AddOrSelectTab(string tabHeader, UIElement customContent = null)
        {
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
            AddOrSelectTab("아이템 목록", itemListView);
        }

        private void Menu_Test_Click(object sender, RoutedEventArgs e)
        {
            TestResultListView testListView = new TestResultListView();
            AddOrSelectTab("테스트결과 목록", testListView);
        }

        private void Menu_Code_Click(object sender, RoutedEventArgs e)
        {
            CodeListView codeListView = new CodeListView();
            AddOrSelectTab("코드 목록", codeListView);
        }

        private void Menu_CoaReg_Click(object sender, RoutedEventArgs e)
        {
            CoARegistryView registryView = new CoARegistryView();
            AddOrSelectTab("CoA 발행 등록", registryView);
        }

        private void Menu_CoaInq_Click(object sender, RoutedEventArgs e)
        {
            AddOrSelectTab("CoA 조회");
        }

        /// <summary>
        /// DB 상태 변경 시 호출. Online/Offline에 따라 상태 표시줄과 [DB 체크] 버튼의 가시성을 갱신한다.
        /// </summary>
        /// <param name="isOnline">네트워크 DB 접속 가능 여부</param>
        private void OnDbStatusChanged(bool isOnline)
        {
            // UI 스레드에서 실행 보장
            Dispatcher.Invoke(() =>
            {
                if (isOnline)
                {
                    TxtDbStatus.Text = $"Online ({DatabaseManager.ActiveDbPath})";
                    TxtDbStatus.Foreground = System.Windows.Media.Brushes.DodgerBlue;
                    DbStatusIndicator.Fill = System.Windows.Media.Brushes.DodgerBlue;
                    BtnDbCheck.Visibility = Visibility.Collapsed;
                }
                else
                {
                    TxtDbStatus.Text = $"Offline ({DatabaseManager.ActiveDbPath})";
                    TxtDbStatus.Foreground = System.Windows.Media.Brushes.Tomato;
                    DbStatusIndicator.Fill = System.Windows.Media.Brushes.Tomato;
                    BtnDbCheck.Visibility = Visibility.Visible;
                }
            });
        }

        /// <summary>
        /// [DB 체크] 버튼 클릭 시 네트워크 DB 재연결을 시도한다.
        /// </summary>
        private void BtnDbCheck_Click(object sender, RoutedEventArgs e)
        {
            DatabaseManager.TryReconnectOnline();
        }
    }
}