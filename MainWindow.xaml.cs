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
                    ImportCsvToSqlite(openFileDialog.FileName);
                    MessageBox.Show("데이터 임포트가 성공적으로 완료되었습니다.", "완료", MessageBoxButton.OK, MessageBoxImage.Information);
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

        private void ImportCsvToSqlite(string filePath)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, System.Text.Encoding.GetEncoding("euc-kr")))
            {
                if (reader.EndOfStream) return;

                string firstLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(firstLine)) return;

                string[] rawHeaders = firstLine.Split(',');
                string[] cleanedHeaders = rawHeaders.Select(h => h.Replace("#", "").Replace("\"", "").Trim()).ToArray();

                string firstField = cleanedHeaders[0].Trim();
                string tableName = "";
                List<string> primaryKeys = new List<string>();

                if (firstField.Contains("pt2_part"))
                {
                    tableName = "pt2_mstr";
                    primaryKeys.Add("pt2_domain");
                    primaryKeys.Add("pt2_part");
                }
                else if (firstField.Contains("zx_code_fldname"))
                {
                    tableName = "zx_code_mstr";
                    primaryKeys.Add("zx_code_fldname");
                    primaryKeys.Add("zx_code_value");
                }
                else if (firstField.Contains("qmir_no"))
                {
                    tableName = "qmir_det";
                    primaryKeys.Add("qmir_batch");
                    primaryKeys.Add("qmir_no");
                }
                else
                {
                    throw new Exception($"알 수 없는 구조의 CSV 형식입니다.\n인식된 첫 필드명: [{firstField}]");
                }

                // 🎯 [수정] App.ConnectionString을 사용하여 정확한 db_files 내부 경로로 오픈
                using (var conn = new SqliteConnection(App.ConnectionString))
                {
                    conn.Open();

                    CreateDynamicTableIfNotExist(conn, tableName, cleanedHeaders, primaryKeys);

                    using (var transaction = conn.BeginTransaction())
                    {
                        using (TextFieldParser parser = new TextFieldParser(reader))
                        {
                            parser.TextFieldType = FieldType.Delimited;
                            parser.SetDelimiters(",");
                            parser.HasFieldsEnclosedInQuotes = true;

                            while (!parser.EndOfData)
                            {
                                string[] fields = parser.ReadFields();
                                if (fields == null || fields.Length == 0) continue;

                                ExecuteUpsert(conn, transaction, tableName, cleanedHeaders, fields, primaryKeys);
                            }
                        }
                        transaction.Commit();
                    }
                }
            }
        }

        private void CreateDynamicTableIfNotExist(SqliteConnection conn, string tableName, string[] headers, List<string> primaryKeys)
        {
            var columnDefinitions = new List<string>();

            foreach (var header in headers)
            {
                columnDefinitions.Add($"{header} TEXT");
            }

            string pkConstraint = $"PRIMARY KEY ({string.Join(", ", primaryKeys)})";
            columnDefinitions.Add(pkConstraint);

            StringBuilder sql = new StringBuilder();
            sql.AppendLine($"CREATE TABLE IF NOT EXISTS {tableName} (");
            sql.AppendLine(string.Join(",\n", columnDefinitions));
            sql.AppendLine(");");

            using (var cmd = new SqliteCommand(sql.ToString(), conn))
            {
                cmd.ExecuteNonQuery();
            }
        }

        private void ExecuteUpsert(SqliteConnection conn, SqliteTransaction trans, string tableName, string[] headers, string[] fields, List<string> keys)
        {
            int length = Math.Min(headers.Length, fields.Length);

            var columns = new List<string>();
            var parameters = new List<string>();
            var updates = new List<string>();

            for (int i = 0; i < length; i++)
            {
                columns.Add(headers[i]);
                parameters.Add($"@{headers[i]}");

                if (!keys.Contains(headers[i]))
                {
                    updates.Add($"{headers[i]} = EXCLUDED.{headers[i]}");
                }
            }

            StringBuilder sql = new StringBuilder();
            sql.AppendLine($"INSERT INTO {tableName} ({string.Join(", ", columns)})");
            sql.AppendLine($"VALUES ({string.Join(", ", parameters)})");
            sql.AppendLine($"ON CONFLICT ({string.Join(", ", keys)}) DO UPDATE SET");
            sql.AppendLine(string.Join(", ", updates));

            using (var cmd = new SqliteCommand(sql.ToString(), conn, trans))
            {
                for (int i = 0; i < length; i++)
                {
                    cmd.Parameters.AddWithValue($"@{headers[i]}", fields[i] ?? (object)DBNull.Value);
                }
                cmd.ExecuteNonQuery();
            }
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
    }
}