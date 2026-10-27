using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace QCS // TODO: 실제 WPF 프로젝트의 네임스페이스로 변경
{
    public partial class DatabaseSchemaWindow : Window
    {
        private readonly string _connectionString;
        private List<SchemaObject> _objects = new();
        private sealed class SchemaObject
        {
            public string Name { get; init; } = "";
            public string Type { get; init; } = "";
            public string Sql { get; init; } = "";
            public string DisplayName => $"[{Type}] {Name}";
        }

        // 기존 DatabaseManager에서 사용하는 연결 문자열을 전달받습니다.
        public DatabaseSchemaWindow(string connectionString)
        {
            InitializeComponent();
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        private void Window_Loaded(object sender, RoutedEventArgs e) => LoadSchema();
        private void Refresh_Click(object sender, RoutedEventArgs e) => LoadSchema();
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private SqliteConnection OpenReadOnlyConnection()
        {
            var builder = new SqliteConnectionStringBuilder(_connectionString)
            {
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };
            var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            return connection;
        }

        private void LoadSchema()
        {
            try
            {
                using var connection = OpenReadOnlyConnection();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT type, name, COALESCE(sql,'') FROM sqlite_master " +
                                  "WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY type,name";
                var objects = new List<SchemaObject>();
                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                        objects.Add(new SchemaObject { Type = reader.GetString(0), Name = reader.GetString(1), Sql = reader.GetString(2) });
                _objects = objects;
                TableList.ItemsSource = _objects;
                DbInfo.Text = $"DB: {new SqliteConnectionStringBuilder(_connectionString).DataSource}  |  읽기 전용  |  테이블: {_objects.Count(x => x.Type == "table")}개 / 뷰: {_objects.Count(x => x.Type == "view")}개";
                StatusText.Text = "조회 완료";
                if (_objects.Count > 0) TableList.SelectedIndex = 0;
                else ClearDetail();
            }
            catch (Exception ex)
            {
                StatusText.Text = "조회 실패";
                MessageBox.Show($"DB 구조 조회 실패: {ex.Message}", "QCS", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

        private static DataTable QueryPragma(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var table = new DataTable();
            for (int i = 0; i < reader.FieldCount; i++) table.Columns.Add(reader.GetName(i));
            while (reader.Read())
            {
                var row = table.NewRow();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i).ToString() ?? "";
                table.Rows.Add(row);
            }
            return table;
        }

        private void TableList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TableList.SelectedItem is not SchemaObject selected) return;
            try
            {
                using var connection = OpenReadOnlyConnection();
                var escaped = QuoteIdentifier(selected.Name);
                ColumnGrid.ItemsSource = QueryPragma(connection, $"PRAGMA table_xinfo({escaped})").DefaultView;
                ForeignKeyGrid.ItemsSource = QueryPragma(connection, $"PRAGMA foreign_key_list({escaped})").DefaultView;
                IndexGrid.ItemsSource = QueryPragma(connection, $"PRAGMA index_list({escaped})").DefaultView;
                CreateSqlText.Text = selected.Sql;
                StatusText.Text = $"{selected.Name} 조회 완료";
            }
            catch (Exception ex)
            {
                ClearDetail();
                StatusText.Text = "상세 조회 실패";
                MessageBox.Show(ex.Message, "QCS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ClearDetail()
        {
            ColumnGrid.ItemsSource = null;
            ForeignKeyGrid.ItemsSource = null;
            IndexGrid.ItemsSource = null;
            CreateSqlText.Text = "";
        }
    }
}
