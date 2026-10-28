using System;
using System.Data;
using System.IO;
using System.Windows;
using Microsoft.Data.Sqlite;
using UserControl = System.Windows.Controls.UserControl;
using MessageBox = System.Windows.MessageBox;
namespace QCS { public partial class DatabaseSchemaView : UserControl {
 public DatabaseSchemaView() { InitializeComponent(); Loaded += (_,__) => RefreshData(); }
 private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshData();
 
 private void RefreshData() { try {
 string path = DatabaseManager.ActiveDbPath;
 if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("DB 파일이 없습니다.",path);
 var b = new SqliteConnectionStringBuilder { DataSource=path,Mode=SqliteOpenMode.ReadOnly,DefaultTimeout=10,Pooling=false };
 using var conn = new SqliteConnection(b.ToString()); conn.Open();
 using var cmd = conn.CreateCommand(); cmd.CommandText = @"SELECT type, name, tbl_name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name"; 
 using var reader = cmd.ExecuteReader(); var data = new DataTable();
 for(int i=0;i<reader.FieldCount;i++) data.Columns.Add(reader.GetName(i),typeof(string));
 while(reader.Read()) { var row=data.NewRow(); for(int i=0;i<reader.FieldCount;i++) row[i]=reader.IsDBNull(i)?"":Convert.ToString(reader.GetValue(i))??""; data.Rows.Add(row); }
 ResultsGrid.ItemsSource=data.DefaultView; StatusText.Text=$"조회 {data.Rows.Count}건 | {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
 } catch(Exception ex) { StatusText.Text="조회 실패: "+ex.Message; MessageBox.Show(ex.Message,"DB 조회 오류",MessageBoxButton.OK,MessageBoxImage.Error); } }
 } }