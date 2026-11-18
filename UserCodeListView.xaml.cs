using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Data.Sqlite;
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;
using DockPanel = System.Windows.Controls.DockPanel;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using Orientation = System.Windows.Controls.Orientation;

namespace QCS;
public partial class UserCodeListView : UserControl
{
    private const string Meta = "QCS_USER_CODE_TYPES", Program = "UserCodeListView.xaml.cs";
    private static readonly string[] Names = { "fldname", "value", "value_up", "cmmt", "desc1", "desc2", "rmk", "char1", "char2", "num1", "num2" };
    private static readonly string[] Labels = { "Fldname", "Value", "Value Up", "Cmmt", "Desc1", "Desc2", "Remark", "Char1", "Char2", "Num1", "Num2" };
    private static readonly string[] Cols = Names.Select(n => "zx_code_" + n).ToArray();
    private readonly TextBox[] inputs = new TextBox[11];
    private bool ready, editing, adding, busy;
    private bool? lastWide;
    private string[]? original;
    private string? actorId;
    private sealed record CodeType(string Key, string Label, string Permission);
    private MainWindow? Main => Window.GetWindow(this) as MainWindow;
    private string TypeKey => Types.SelectedValue as string ?? "";
    private void Report(string s) => Main?.SetRunStatus(s);
    private static SqliteConnection Open() { var c = new SqliteConnection(DatabaseManager.GetConnectionString("common")); c.Open(); return c; }
    private QcsUser? Actor(SqliteConnection c, SqliteTransaction? tx = null)
    {
        var id = Main?.GetCurrentUser()?.WindowsId;
        if (id == null) return null;
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT zx_code_desc1,zx_code_desc2,zx_code_cmmt FROM zx_code_mstr WHERE zx_code_fldname='QCS_USERS' AND zx_code_value=$id COLLATE NOCASE"; q.Parameters.AddWithValue("$id",id);
        using var r=q.ExecuteReader(); if(!r.Read()) return null;
        return new QcsUser(id, Convert.ToString(r[0])??"",Convert.ToString(r[1])??"",StartupUserService.ParseLevel(Convert.ToString(r[2])));
    }
    private static bool SafeType(string s) => !string.IsNullOrWhiteSpace(s) && !s.StartsWith("QCS_",StringComparison.OrdinalIgnoreCase) && !s.StartsWith("MSDS_",StringComparison.OrdinalIgnoreCase) && !s.Equals("API_model",StringComparison.OrdinalIgnoreCase) && !new[]{"CoA_Delete","CoA_SQL_fields","CoA_files","CoA_sql_connection","CoA_CS_db_path","CoA_CS_xltm_path","CoA_CS_Last_Reserve_Date","CoA_CS_Next_Num","CoA_CS_Start_Num"}.Contains(s,StringComparer.OrdinalIgnoreCase);
    private static bool Allowed(QcsUser u,string permission) => u.UserLevel is 9 or 8 || permission.Trim()=="*" || permission.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Contains(u.WindowsId,StringComparer.OrdinalIgnoreCase);
    private List<CodeType> GetTypes(SqliteConnection c,QcsUser u,SqliteTransaction? tx=null)
    {
        using var q=c.CreateCommand(); q.Transaction=tx; q.CommandText="SELECT zx_code_value,zx_code_desc1,zx_code_cmmt FROM zx_code_mstr WHERE zx_code_fldname=$m ORDER BY zx_code_value";q.Parameters.AddWithValue("$m",Meta);
        using var r=q.ExecuteReader();var list=new List<CodeType>();while(r.Read()){string k=Convert.ToString(r[0])??"",d=Convert.ToString(r[1])??"",p=Convert.ToString(r[2])??"";if(SafeType(k)&&Allowed(u,p))list.Add(new(k,string.IsNullOrWhiteSpace(d)?k:d,p));}return list;
    }
    private static bool ProgramAllowed(SqliteConnection c,QcsUser u,SqliteTransaction? tx)
    {
        if(!QcsAuthorizationService.IsActive(u.UserLevel))return false;
        if(u.UserLevel==9)return true;
        using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="SELECT zx_code_cmmt,zx_code_char1 FROM zx_code_mstr WHERE zx_code_fldname='QCS_APPLICATIONS' AND zx_code_value=$id";q.Parameters.AddWithValue("$id",Program);using var r=q.ExecuteReader();return r.Read()&&!string.Equals(Convert.ToString(r[1])?.Trim(),"Y",StringComparison.OrdinalIgnoreCase)&&Allowed(u,Convert.ToString(r[0])??"");
    }
    private bool Authorized(SqliteConnection c,string type,SqliteTransaction? tx=null,bool manage=false)
    {
        var u=Actor(c,tx); return u!=null && u.WindowsId==actorId && ProgramAllowed(c,u,tx) && (!manage || u.UserLevel is 9 or 8) && GetTypes(c,u,tx).Any(x=>x.Key==type);
    }
    public UserCodeListView()
    {
        InitializeComponent();
        foreach(var i in new[]{1,3,4,5,6,7,8,9,10,0,2}) Rows.Columns.Add(new DataGridTextColumn{Header=Labels[i],Binding=new System.Windows.Data.Binding("["+Cols[i]+"]"),Width=i==3?200: i==1?130:100,ElementStyle=(Style)FindResource("QcsGridText")});
        for(int i=0;i<inputs.Length;i++) inputs[i]=new TextBox{Style=(Style)FindResource("QcsInput"),Height=i==3?128:i==6?60:30,AcceptsReturn=i==3||i==6,TextWrapping=i==3||i==6?TextWrapping.Wrap:TextWrapping.NoWrap,VerticalScrollBarVisibility=i==3||i==6?ScrollBarVisibility.Auto:ScrollBarVisibility.Disabled,VerticalContentAlignment=i==3||i==6?VerticalAlignment.Top:VerticalAlignment.Center};
        Fields.SizeChanged+=(_,_)=>LayoutFields(); SizeChanged+=(_,_)=>ResizeCmmt(); LayoutFields();
        Loaded+=(_,_)=>{if(!ready){ready=true;Run(LoadTypes);}}; SetEditing(false);
    }
    private void ResizeCmmt() { inputs[3].Height=Math.Max(120,ActualHeight-(lastWide==true?440:570)); }
    private void LayoutFields()
    {
        bool wide=Fields.ActualWidth>=560; if(lastWide==wide)return; lastWide=wide; ResizeCmmt();
        foreach(var input in inputs) if(input.Parent is DockPanel parent) parent.Children.Remove(input);
        Fields.Children.Clear();Fields.RowDefinitions.Clear();Fields.ColumnDefinitions.Clear();
        Fields.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});if(wide)Fields.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        int row=0; foreach(var group in new[]{new[]{0},new[]{1,2},new[]{4},new[]{5},new[]{3},new[]{6},new[]{7,8},new[]{9,10}}){int col=0;foreach(int i in group){if(col==0)Fields.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});var panel=new DockPanel{Margin=new Thickness(0,0,8,8)};var label=new TextBlock{Text=Labels[i],Width=78,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,5,0,0)};DockPanel.SetDock(label,Dock.Left);panel.Children.Add(label);panel.Children.Add(inputs[i]);Grid.SetRow(panel,row);Grid.SetColumn(panel,col);if(wide&&group.Length==1)Grid.SetColumnSpan(panel,2);Fields.Children.Add(panel);if(wide&&group.Length==2&&col==0)col=1;else{row++;col=0;}}}
    }
    private void Run(Action action){try{action();}catch(Exception ex){Report("업무코드 오류: "+ex.Message);MessageBox.Show(ex.Message,"업무코드 오류",MessageBoxButton.OK,MessageBoxImage.Error);}}
    private void LoadTypes(){using var c=Open();var u=Actor(c);actorId=u?.WindowsId;busy=true;Types.ItemsSource=u!=null&&QcsAuthorizationService.CanAccessProgram(u,Program)?GetTypes(c,u):new List<CodeType>();Types.SelectedIndex=Types.Items.Count>0?0:-1;PermissionButton.Visibility=u?.UserLevel is 9 or 8?Visibility.Visible:Visibility.Collapsed;busy=false;LoadRows();}
    private void LoadRows(string? select=null){using var c=Open();if(!Authorized(c,TypeKey)){Rows.ItemsSource=null;Count.Text="조회 권한이 있는 코드가 없습니다.";Fill(null);return;}using var q=c.CreateCommand();q.CommandText="SELECT "+string.Join(",",Cols)+" FROM zx_code_mstr WHERE zx_code_fldname=$t ORDER BY zx_code_value";q.Parameters.AddWithValue("$t",TypeKey);using var r=q.ExecuteReader();var dt=new DataTable();dt.Load(r);var words=Search.Text.Split(' ',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);if(words.Length>0){bool all=Mode.SelectedIndex==0;foreach(var record in dt.Rows.Cast<DataRow>().ToArray()){bool Match(string word)=>record.ItemArray.Any(v=>(Convert.ToString(v)??"").Contains(word,StringComparison.OrdinalIgnoreCase));if(!(all?words.All(Match):words.Any(Match)))dt.Rows.Remove(record);}}Rows.ItemsSource=dt.DefaultView;Rows.SelectedItem=dt.DefaultView.Cast<DataRowView>().FirstOrDefault(x=>Convert.ToString(x[Cols[1]])==select)??dt.DefaultView.Cast<DataRowView>().FirstOrDefault();Count.Text=$"전체 {dt.Rows.Count:N0}건";Fill(Rows.SelectedItem as DataRowView);Report($"{TypeKey} 조회 완료 ({dt.Rows.Count:N0}건)");}
    private void Fill(DataRowView? row){original=row==null?null:Cols.Select(c=>Convert.ToString(row[c])??"").ToArray();for(int i=0;i<11;i++)inputs[i].Text=original?[i]??"";SetEditing(false);}
    private void SetEditing(bool value){editing=value;Types.IsEnabled=Rows.IsEnabled=Search.IsEnabled=Mode.IsEnabled=!value;NewButton.IsEnabled=!value&&TypeKey!="";EditButton.IsEnabled=!value&&original!=null;PermissionButton.IsEnabled=!value;SaveButton.IsEnabled=CancelButton.IsEnabled=value;for(int i=0;i<11;i++){inputs[i].IsReadOnly=!value||i==0||i==2||(i==1&&!adding);inputs[i].Background=inputs[i].IsReadOnly ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226,234,245)) : System.Windows.Media.Brushes.White;}}
    public bool CanDiscardChanges()=>!editing||MessageBox.Show("저장하지 않은 변경을 취소하시겠습니까?","확인",MessageBoxButton.YesNo)==MessageBoxResult.Yes;
    private void TypeChanged(object s,SelectionChangedEventArgs e){if(ready&&!busy&&!editing)Run(()=>LoadRows());}
    private void RowChanged(object s,SelectionChangedEventArgs e){if(ready&&!editing)Fill(Rows.SelectedItem as DataRowView);}
    private void SearchClick(object s,RoutedEventArgs e){if(!editing)Run(()=>LoadRows());}
    private void RefreshClick(object s,RoutedEventArgs e){if(!editing)Run(LoadTypes);}
    private void NewClick(object s,RoutedEventArgs e){adding=true;foreach(var t in inputs)t.Clear();inputs[0].Text=TypeKey;inputs[9].Text=inputs[10].Text="0";SetEditing(true);inputs[1].Focus();}
    private void EditClick(object s,RoutedEventArgs e){if(original!=null){adding=false;SetEditing(true);}}
    private void CancelClick(object s,RoutedEventArgs e){Fill(Rows.SelectedItem as DataRowView);Report("업무코드 편집 취소");}
    private void SaveClick(object s,RoutedEventArgs e)=>Run(Write);
    private void Write()
    {
        var values=inputs.Select(x=>x.Text).ToArray();long n1=0,n2=0;if(string.IsNullOrWhiteSpace(values[1])||(!long.TryParse(values[9],out n1)||!long.TryParse(values[10],out n2))){Report("Value 및 숫자 필드를 확인하세요.");return;}
        using var c=Open();using var tx=c.BeginTransaction();if(!Authorized(c,TypeKey,tx)){Report("코드 관리권한이 없습니다.");return;}using var q=c.CreateCommand();q.Transaction=tx;
        bool insert=adding;var key=insert?values[1]:original![1];q.Parameters.AddWithValue("$t",TypeKey);q.Parameters.AddWithValue("$key",key);
        if(insert){q.CommandText="INSERT OR IGNORE INTO zx_code_mstr ("+string.Join(",",Cols)+") VALUES ($t,$key,"+string.Join(",",Enumerable.Range(2,9).Select(i=>"$v"+i))+")";}
        else {string where=" WHERE zx_code_fldname=$t AND zx_code_value=$key AND "+string.Join(" AND ",Enumerable.Range(2,9).Select(i=>"COALESCE(CAST("+Cols[i]+" AS TEXT),'')=$o"+i));foreach(int i in Enumerable.Range(2,9))q.Parameters.AddWithValue("$o"+i,original![i]);q.CommandText="UPDATE zx_code_mstr SET "+string.Join(",",Enumerable.Range(3,8).Select(i=>Cols[i]+"=$v"+i))+where;}
        for(int i=insert?2:3;i<11;i++)q.Parameters.AddWithValue("$v"+i,i==9?(object)n1:i==10?(object)n2:values[i]);
        if(q.ExecuteNonQuery()!=1){Report("중복 코드 또는 다른 사용자의 변경이 있습니다. 새로고침 후 확인하세요.");return;}tx.Commit();SetEditing(false);adding=false;LoadRows(key);Report("업무코드 저장 완료");
    }
    private void PermissionClick(object s,RoutedEventArgs e)=>Run(()=>{
        using var c=Open();if(!Authorized(c,TypeKey,manage:true)){Report("관리권한 설정 권한이 없습니다.");return;}string type=TypeKey;var u=Actor(c)!;var old=GetTypes(c,u).Single(x=>x.Key==type).Permission;
        var dialog=new Window{Title="코드 관리권한 설정 - "+type,Owner=Main,Width=540,Height=570,WindowStartupLocation=WindowStartupLocation.CenterOwner};var panel=new DockPanel{Margin=new Thickness(16)};var all=new CheckBox{Content="전체 활성 사용자 허용",IsChecked=old.Trim()=="*",Margin=new Thickness(0,0,0,10)};DockPanel.SetDock(all,Dock.Top);panel.Children.Add(all);var filter=new TextBox{Style=(Style)FindResource("QcsInput"),ToolTip="사용자 ID 또는 이름 검색",Margin=new Thickness(0,0,0,8)};DockPanel.SetDock(filter,Dock.Top);panel.Children.Add(filter);var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=System.Windows.HorizontalAlignment.Right};DockPanel.SetDock(buttons,Dock.Bottom);panel.Children.Add(buttons);var save=new Button{Content="저장",Style=(Style)FindResource("QcsPrimaryButton")};var cancel=new Button{Content="취소",IsCancel=true,Style=(Style)FindResource("QcsPrimaryButton")};buttons.Children.Add(save);buttons.Children.Add(cancel);var list=new StackPanel();panel.Children.Add(new ScrollViewer{Content=list});var checks=new List<CheckBox>();using(var q=c.CreateCommand()){q.CommandText="SELECT zx_code_value,zx_code_desc1,zx_code_desc2 FROM zx_code_mstr WHERE zx_code_fldname='QCS_USERS' AND zx_code_cmmt='1' ORDER BY zx_code_value";using var r=q.ExecuteReader();while(r.Read()){string id=Convert.ToString(r[0])??"";var check=new CheckBox{Tag=id,Content=id+"  /  "+r[1]+"  /  "+r[2],IsChecked=old.Split(',',StringSplitOptions.TrimEntries).Contains(id,StringComparer.OrdinalIgnoreCase),Margin=new Thickness(0,5,0,5)};checks.Add(check);list.Children.Add(check);}}
        filter.TextChanged+=(_,_)=>{foreach(var check in checks)check.Visibility=(Convert.ToString(check.Content)??"").Contains(filter.Text,StringComparison.OrdinalIgnoreCase)?Visibility.Visible:Visibility.Collapsed;}; all.Checked+=(_,_)=>{foreach(var check in checks)check.IsEnabled=false;};all.Unchecked+=(_,_)=>{foreach(var check in checks)check.IsEnabled=true;};foreach(var check in checks)check.IsEnabled=all.IsChecked!=true;
        dialog.Content=panel;save.Click+=(_,_)=>dialog.DialogResult=true;if(QcsModal.Show(dialog,Main!)!=true){Report("관리권한 설정 취소");return;}
        using var tx=c.BeginTransaction();if(!Authorized(c,type,tx,true)){Report("관리권한이 변경되었습니다.");return;}using var update=c.CreateCommand();update.Transaction=tx;update.CommandText="UPDATE zx_code_mstr SET zx_code_cmmt=$p WHERE zx_code_fldname=$m AND zx_code_value=$t AND COALESCE(zx_code_cmmt,'')=$old";update.Parameters.AddWithValue("$p",all.IsChecked==true?"*":string.Join(",",checks.Where(x=>x.IsChecked==true).Select(x=>(string)x.Tag)));update.Parameters.AddWithValue("$m",Meta);update.Parameters.AddWithValue("$t",type);update.Parameters.AddWithValue("$old",old);if(update.ExecuteNonQuery()!=1){Report("다른 사용자가 권한을 변경했습니다.");return;}tx.Commit();Report("코드 관리권한 저장 완료");
    });
}
