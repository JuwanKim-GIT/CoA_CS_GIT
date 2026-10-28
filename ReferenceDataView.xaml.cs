using System;
using System.Data;
using System.IO;
using System.Windows;
using Microsoft.Data.Sqlite;
using UserControl = System.Windows.Controls.UserControl;
using MessageBox = System.Windows.MessageBox;
namespace QCS { public partial class ReferenceDataView : UserControl {
 public ReferenceDataView() { InitializeComponent(); Loaded += (_,__) => RefreshData(); }
 private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshData();
 private void Clear_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); RefreshData(); }
 private void RefreshData() { try {
 string path = DatabaseManager.ActiveDbPath;
 if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("DB 파일이 없습니다.",path);
 var b = new SqliteConnectionStringBuilder { DataSource=path,Mode=SqliteOpenMode.ReadOnly,DefaultTimeout=10,Pooling=false };
 using var conn = new SqliteConnection(b.ToString()); conn.Open();
 using var cmd = conn.CreateCommand(); cmd.CommandText = @"SELECT cas_ref_cas_id,cas_ref_cas_no,cas_ref_toxic_substance,cas_ref_accident_substance,cas_ref_restricted_prohibited,cas_ref_priority_regulated,cas_ref_persistent_pollutant,cas_ref_emission_survey,cas_ref_controlled_substance,cas_ref_special_control,cas_ref_workplace_measure,cas_ref_updated_at FROM cas_ref_mstr WHERE ($search = '' OR cas_ref_cas_no LIKE '%' || $search || '%') ORDER BY cas_ref_cas_no LIMIT 2000"; cmd.Parameters.AddWithValue("$search", SearchBox.Text.Trim());
 using var reader = cmd.ExecuteReader(); var data = new DataTable();
 for(int i=0;i<reader.FieldCount;i++) data.Columns.Add(reader.GetName(i),typeof(string));
 while(reader.Read()) { var row=data.NewRow(); for(int i=0;i<reader.FieldCount;i++) row[i]=reader.IsDBNull(i)?"":Convert.ToString(reader.GetValue(i))??""; data.Rows.Add(row); }
 ResultsGrid.ItemsSource=data.DefaultView; StatusText.Text=$"조회 {data.Rows.Count}건 | {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
 } catch(Exception ex) { StatusText.Text="조회 실패: "+ex.Message; MessageBox.Show(ex.Message,"DB 조회 오류",MessageBoxButton.OK,MessageBoxImage.Error); } }
 } }