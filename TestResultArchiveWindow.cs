using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Window = System.Windows.Window;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using DataGrid = System.Windows.Controls.DataGrid;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace QCS
{
    internal static class TestResultArchiveStore
    {
        private static readonly string[] Columns = { "no","charac","uom","batch","first_value","last_value","ltol","utol","char1","char2","char3","char4","deci1","deci2","deci3","deci4","upd_date","upd_psn","reg_date","reg_psn","part","mf_date" };
        internal static void Ensure(SqliteConnection conn)
        {
            // Archive schema mirrors the existing active table, without changing it.
            using var pragma = new SqliteCommand("PRAGMA table_info(qmir_det)",conn);
            var fields = new List<string>();
            using (var reader = pragma.ExecuteReader())
            {
                while (reader.Read()) fields.Add("qmirh_"+reader.GetString(1).Substring(5)+" "+reader.GetString(2));
            }
            if (fields.Count != Columns.Length) throw new InvalidOperationException("qmir_det 필드 구조가 예상과 다릅니다. Archive 생성이 중단되었습니다.");
            fields.Add("qmirh_archive_date TEXT");
            fields.Add("PRIMARY KEY(qmirh_no,qmirh_batch)");
            using var cmd = new SqliteCommand("CREATE TABLE IF NOT EXISTS qmirh_hist ("+string.Join(",",fields)+")",conn);
            cmd.ExecuteNonQuery();
        }
        internal static string DateExpression(string field)
        {
            // Normalize the date formats used by Test Result records for chronological comparisons.
            // Both yy/MM/dd (legacy DB) and yy-MM-dd (newer input) represent the same date.
            return "(CASE " +
                "WHEN " + field + " GLOB '[0-9][0-9]/[0-9][0-9]/[0-9][0-9]' " +
                "OR " + field + " GLOB '[0-9][0-9]-[0-9][0-9]-[0-9][0-9]' " +
                "THEN '20'||substr(" + field + ",1,2)||'-'||substr(" + field + ",4,2)||'-'||substr(" + field + ",7,2) " +
                "WHEN " + field + " GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]' " +
                "OR " + field + " GLOB '[0-9][0-9][0-9][0-9]/[0-9][0-9]/[0-9][0-9]' " +
                "THEN substr(" + field + ",1,4)||'-'||substr(" + field + ",6,2)||'-'||substr(" + field + ",9,2) " +
                "ELSE NULL END)";
        }
        internal static string ArchiveDatePredicate(bool reg, bool upd, bool mf, string alias = "qmir_")
        {
            var fields = new List<string>();
            if (reg) fields.Add("reg_date");
            if (upd) fields.Add("upd_date");
            if (mf) fields.Add("mf_date");
            if (fields.Count == 0) throw new ArgumentException("날짜 조건을 하나 이상 선택해야 합니다.");
            return string.Join(" AND ", fields.Select(x =>
                "(@from IS NULL OR " + DateExpression(alias+x) + " >= @from) AND " +
                "(@to IS NULL OR " + DateExpression(alias+x) + " <= @to) AND " +
                DateExpression(alias+x) + " IS NOT NULL"));
        }
        internal static void AddDateRange(SqliteCommand cmd, DateTime? from, DateTime? to)
        {
            cmd.Parameters.AddWithValue("@from", from.HasValue ? (object)from.Value.ToString("yyyy-MM-dd") : DBNull.Value);
            cmd.Parameters.AddWithValue("@to", to.HasValue ? (object)to.Value.ToString("yyyy-MM-dd") : DBNull.Value);
        }
        internal sealed class DateRanges
        {
            internal DateTime? RegFrom, RegTo, UpdFrom, UpdTo, MfFrom, MfTo;
            internal bool Reg, Upd, Mf;
        }
        internal static string ArchiveDatePredicate(DateRanges ranges, string alias="qmir_")
        {
            var conditions = new List<string>();
            foreach (var item in new[] {
                (enabled:ranges.Reg,field:"reg_date",from:"@regFrom",to:"@regTo"),
                (enabled:ranges.Upd,field:"upd_date",from:"@updFrom",to:"@updTo"),
                (enabled:ranges.Mf,field:"mf_date",from:"@mfFrom",to:"@mfTo") })
            {
                if (!item.enabled) continue;
                string date = DateExpression(alias + item.field);
                conditions.Add("(" + item.from + " IS NULL OR " + date + " >= " + item.from + ")");
                conditions.Add("(" + item.to + " IS NULL OR " + date + " <= " + item.to + ")");
                conditions.Add(date + " IS NOT NULL");
            }
            if (conditions.Count == 0) throw new ArgumentException("날짜 조건을 하나 이상 선택해야 합니다.");
            return string.Join(" AND ", conditions);
        }
        internal static void AddDateRange(SqliteCommand cmd, DateRanges ranges)
        {
            foreach (var item in new[] { ("@regFrom",ranges.RegFrom),("@regTo",ranges.RegTo),
                ("@updFrom",ranges.UpdFrom),("@updTo",ranges.UpdTo),
                ("@mfFrom",ranges.MfFrom),("@mfTo",ranges.MfTo) })
                cmd.Parameters.AddWithValue(item.Item1, item.Item2.HasValue ? (object)item.Item2.Value.ToString("yyyy-MM-dd") : DBNull.Value);
        }
        internal static void MoveBatches(DateTime cutoff, bool reg, bool upd, bool mf, out int batches, out int records)
        {
            batches=0;records=0;
            using var conn = new SqliteConnection(DatabaseManager.GetConnectionString("quality")); conn.Open(); Ensure(conn);
            using var tx = conn.BeginTransaction();
            // A batch qualifies only if every row has three valid dates before cutoff.
            string predicate = ArchiveDatePredicate(reg,upd,mf);
            string batchSql = "SELECT qmir_batch FROM qmir_det GROUP BY qmir_batch HAVING COUNT(*)=SUM(CASE WHEN "+predicate+" THEN 1 ELSE 0 END)";
            var keys = new List<string>();
            using (var cmd = new SqliteCommand(batchSql,conn,tx))
            {
                AddDateRange(cmd, null, cutoff.AddDays(-1));
                using var reader = cmd.ExecuteReader(); while (reader.Read()) keys.Add(reader.GetString(0));
            }
            string source = string.Join(",",Columns.Select(x=>"qmir_"+x));
            string target = string.Join(",",Columns.Select(x=>"qmirh_"+x));
            foreach (var batch in keys)
            {
                using var insert = new SqliteCommand("INSERT INTO qmirh_hist ("+target+",qmirh_archive_date) SELECT "+source+",@today FROM qmir_det WHERE qmir_batch=@batch",conn,tx);
                insert.Parameters.AddWithValue("@batch",batch); insert.Parameters.AddWithValue("@today",DateTime.Today.ToString("yy/MM/dd"));
                int moved=insert.ExecuteNonQuery();
                using var delete = new SqliteCommand("DELETE FROM qmir_det WHERE qmir_batch=@batch",conn,tx);
                delete.Parameters.AddWithValue("@batch",batch);
                if (delete.ExecuteNonQuery()!=moved) throw new InvalidOperationException("Archive 이동 건수가 일치하지 않습니다.");
                batches++; records+=moved;
            }
            tx.Commit();
        }
        internal static DataTable EligibleBatches(DateRanges ranges, int offset, int pageSize)
        {
            using var conn = new SqliteConnection(DatabaseManager.GetConnectionString("quality")); conn.Open(); Ensure(conn);
            string predicate = ArchiveDatePredicate(ranges);
            string sql = "SELECT qmir_batch AS [Batch No], MAX(qmir_part) AS [Material No], " +
                "MAX(qmir_reg_date) AS [등록일], MAX(qmir_upd_date) AS [수정일], MAX(qmir_mf_date) AS [제조일자], " +
                "COUNT(*) AS [대상 건수] FROM qmir_det GROUP BY qmir_batch " +
                "HAVING COUNT(*)=SUM(CASE WHEN " + predicate + " THEN 1 ELSE 0 END) " +
                "ORDER BY qmir_batch LIMIT @limit OFFSET @offset";
            using var cmd = new SqliteCommand(sql,conn);
            AddDateRange(cmd, ranges);
            cmd.Parameters.AddWithValue("@limit",pageSize);cmd.Parameters.AddWithValue("@offset",offset);
            using var reader=cmd.ExecuteReader();
            var table=new DataTable();
            for(int i=0;i<reader.FieldCount;i++) table.Columns.Add(reader.GetName(i),typeof(string));
            while(reader.Read()) { var row=table.NewRow(); for(int i=0;i<reader.FieldCount;i++) row[i]=reader.IsDBNull(i)?"":Convert.ToString(reader.GetValue(i))??"";table.Rows.Add(row); }
            return table;
        }
        internal static (int batches,int records) MoveSelectedBatches(IEnumerable<string> selected, DateRanges ranges)
        {
            var batches = selected.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
            if (batches.Count==0) return (0,0);
            using var conn=new SqliteConnection(DatabaseManager.GetConnectionString("quality"));conn.Open();Ensure(conn);
            using var tx=conn.BeginTransaction();
            string predicate=ArchiveDatePredicate(ranges);
            string source=string.Join(",",Columns.Select(x=>"qmir_"+x));
            string target=string.Join(",",Columns.Select(x=>"qmirh_"+x));
            int count=0;
            foreach(string batch in batches)
            {
                using(var validate=new SqliteCommand("SELECT COUNT(*),COALESCE(SUM(CASE WHEN "+predicate+" THEN 1 ELSE 0 END),0) FROM qmir_det WHERE qmir_batch=@batch",conn,tx))
                {
                    validate.Parameters.AddWithValue("@batch",batch);
                    AddDateRange(validate, ranges);
                    using var reader=validate.ExecuteReader();reader.Read();
                    long all=reader.GetInt64(0),eligible=reader.GetInt64(1);
                    if(all==0 || all!=eligible) throw new InvalidOperationException("이관 조건이 변경되었거나 대상이 없습니다: "+batch);
                }
                using var insert=new SqliteCommand("INSERT INTO qmirh_hist ("+target+",qmirh_archive_date) SELECT "+source+",@today FROM qmir_det WHERE qmir_batch=@batch",conn,tx);
                insert.Parameters.AddWithValue("@today",DateTime.Today.ToString("yy/MM/dd"));insert.Parameters.AddWithValue("@batch",batch);
                int moved=insert.ExecuteNonQuery();
                using var delete=new SqliteCommand("DELETE FROM qmir_det WHERE qmir_batch=@batch",conn,tx);
                delete.Parameters.AddWithValue("@batch",batch);
                if(delete.ExecuteNonQuery()!=moved)throw new InvalidOperationException("Archive 이동 건수가 일치하지 않습니다: "+batch);
                count+=moved;
            }
            tx.Commit();return (batches.Count,count);
        }
        internal static (long batches,long records) Preview(DateRanges ranges)
        {
            using var conn = new SqliteConnection(DatabaseManager.GetConnectionString("quality")); conn.Open(); Ensure(conn);
            string predicate = ArchiveDatePredicate(ranges);
            string sql = "SELECT COUNT(*),COALESCE(SUM(n),0) FROM (SELECT qmir_batch,COUNT(*) n FROM qmir_det GROUP BY qmir_batch HAVING COUNT(*)=SUM(CASE WHEN "+predicate+" THEN 1 ELSE 0 END))";
            using var cmd = new SqliteCommand(sql,conn); AddDateRange(cmd, ranges);
            using var reader = cmd.ExecuteReader(); reader.Read(); return (reader.GetInt64(0),reader.GetInt64(1));
        }
        internal static (long rows,long batches) CountMatches(string batch,string part,DateTime? from,DateTime? to)
        {
            using var conn = new SqliteConnection(DatabaseManager.GetConnectionString("quality")); conn.Open(); Ensure(conn);
            var sql = new StringBuilder("SELECT COUNT(*),COUNT(DISTINCT qmirh_batch) FROM qmirh_hist WHERE 1=1 ");
            if (batch.Length>0) sql.Append("AND qmirh_batch LIKE @batch ");
            if (part.Length>0) sql.Append("AND qmirh_part LIKE @part ");
            if (from.HasValue) sql.Append("AND "+DateExpression("qmirh_mf_date")+">=@from ");
            if (to.HasValue) sql.Append("AND "+DateExpression("qmirh_mf_date")+"<=@to ");
            using var cmd=new SqliteCommand(sql.ToString(),conn);
            cmd.Parameters.AddWithValue("@batch","%"+batch+"%");cmd.Parameters.AddWithValue("@part","%"+part+"%");
            if(from.HasValue)cmd.Parameters.AddWithValue("@from",from.Value.ToString("yyyy-MM-dd"));
            if(to.HasValue)cmd.Parameters.AddWithValue("@to",to.Value.ToString("yyyy-MM-dd"));
            using var reader=cmd.ExecuteReader();reader.Read();return (reader.GetInt64(0),reader.GetInt64(1));
        }
        internal static DataTable Search(string batch, string part, DateTime? from, DateTime? to, int offset = 0, int pageSize = 1000)
        {
            using var conn = new SqliteConnection(DatabaseManager.GetConnectionString("quality")); conn.Open(); Ensure(conn);
            var sql = new StringBuilder("SELECT "+string.Join(",",Columns.Select(x=>"qmirh_"+x+" AS ["+x+"]"))+",qmirh_archive_date AS [archive_date] FROM qmirh_hist WHERE 1=1 ");
            if (batch.Length>0) sql.Append("AND qmirh_batch LIKE @batch ");
            if (part.Length>0) sql.Append("AND qmirh_part LIKE @part ");
            if (from.HasValue) sql.Append("AND "+DateExpression("qmirh_mf_date")+">=@from ");
            if (to.HasValue) sql.Append("AND "+DateExpression("qmirh_mf_date")+"<=@to ");
            sql.Append("ORDER BY qmirh_batch,qmirh_no LIMIT @limit OFFSET @offset");
            using var cmd = new SqliteCommand(sql.ToString(),conn);
            cmd.Parameters.AddWithValue("@batch","%"+batch+"%");cmd.Parameters.AddWithValue("@part","%"+part+"%");
            if(from.HasValue)cmd.Parameters.AddWithValue("@from",from.Value.ToString("yyyy-MM-dd"));
            if(to.HasValue)cmd.Parameters.AddWithValue("@to",to.Value.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@limit",pageSize);
            cmd.Parameters.AddWithValue("@offset",offset);
            using var reader = cmd.ExecuteReader();
            // Archive values may contain legacy non-numeric markers (e.g. "?") in
            // columns whose SQLite declared type is numeric. DataTable.Load infers
            // Double and fails on those values. The archive grid is read-only, so
            // represent all displayed fields as strings without modifying DB data.
            var t = new DataTable();
            for (int i = 0; i < reader.FieldCount; i++)
                t.Columns.Add(reader.GetName(i), typeof(string));
            while (reader.Read())
            {
                var row = t.NewRow();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[i] = reader.IsDBNull(i)
                        ? DBNull.Value
                        : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                t.Rows.Add(row);
            }
            return t;
        }
        internal static int Restore(IEnumerable<(string batch,string no)> keys)
        {
            using var conn=new SqliteConnection(DatabaseManager.GetConnectionString("quality"));conn.Open();Ensure(conn);
            using var tx=conn.BeginTransaction();int count=0;
            string dest=string.Join(",",Columns.Select(x=>"qmir_"+x));
            string values=string.Join(",",Columns.Select(x=>x=="upd_date"?"@today":"qmirh_"+x));
            foreach(var key in keys.Distinct())
            {
                using var insert=new SqliteCommand("INSERT INTO qmir_det ("+dest+") SELECT "+values+" FROM qmirh_hist WHERE qmirh_batch=@b AND qmirh_no=@n",conn,tx);
                insert.Parameters.AddWithValue("@today",DateTime.Today.ToString("yy/MM/dd"));insert.Parameters.AddWithValue("@b",key.batch);insert.Parameters.AddWithValue("@n",key.no);
                if(insert.ExecuteNonQuery()!=1)throw new InvalidOperationException("복원 대상이 존재하지 않습니다: "+key.batch+" / "+key.no);
                using var delete=new SqliteCommand("DELETE FROM qmirh_hist WHERE qmirh_batch=@b AND qmirh_no=@n",conn,tx);
                delete.Parameters.AddWithValue("@b",key.batch);delete.Parameters.AddWithValue("@n",key.no);
                if(delete.ExecuteNonQuery()!=1)throw new InvalidOperationException("Archive 삭제 실패");
                count++;
            }
            tx.Commit();return count;
        }
    }
    internal sealed class TestResultArchiveWindow : Window
    {
        private readonly DataGrid grid = new DataGrid();
        private readonly TextBlock info = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBox batchBox = new TextBox { Width=155,Height=28,Margin=new Thickness(4,0,8,0) };
        private readonly TextBox partBox = new TextBox { Width=155,Height=28,Margin=new Thickness(4,0,8,0) };
        private readonly Button previous = new Button { Content="이전",Width=65,Height=28,Margin=new Thickness(4,0,4,0) };
        private readonly Button next = new Button { Content="다음",Width=65,Height=28,Margin=new Thickness(4,0,4,0) };
        private readonly DateTime? from,to;
        private const int PageSize = 1000;
        private int offset, requestId;
        private long total;
        public bool Restored {get;private set;}

        public TestResultArchiveWindow(string batch,string part,DateTime? from,DateTime? to)
        {
            this.from=from;this.to=to;
            batchBox.Text=batch;partBox.Text=part;
            Title="Test Result Archive 조회 / 선택 행 복원";
            Width=1300;Height=750;WindowStartupLocation=WindowStartupLocation.CenterOwner;
            var root=new DockPanel{Margin=new Thickness(12)};Content=root;
            var top=new DockPanel{LastChildFill=true,Margin=new Thickness(0,0,0,8)};
            DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
            var bar=new WrapPanel{Orientation=Orientation.Horizontal};
            DockPanel.SetDock(bar,Dock.Left);top.Children.Add(bar);
            bar.Children.Add(new TextBlock { Text="Batch No",VerticalAlignment=VerticalAlignment.Center });
            bar.Children.Add(batchBox);
            bar.Children.Add(new TextBlock { Text="Material No",VerticalAlignment=VerticalAlignment.Center });
            bar.Children.Add(partBox);
            var search=new Button{Content="조회",Width=65,Height=28,Margin=new Thickness(0,0,10,0)};
            search.Click+=(s,e)=>{offset=0;Refresh();};bar.Children.Add(search);
            batchBox.KeyDown+=(s,e)=>{if(e.Key==System.Windows.Input.Key.Enter){offset=0;Refresh();}};
            partBox.KeyDown+=(s,e)=>{if(e.Key==System.Windows.Input.Key.Enter){offset=0;Refresh();}};
            previous.Click+=(s,e)=>{offset=Math.Max(0,offset-PageSize);Refresh();};
            next.Click+=(s,e)=>{offset+=PageSize;Refresh();};
            bar.Children.Add(previous);bar.Children.Add(next);
            var restore=new Button{Content="선택 행 복원",Width=125,Height=28,Margin=new Thickness(0,0,12,0)};
            restore.Click+=(s,e)=>RestoreSelected();bar.Children.Add(restore);
            info.HorizontalAlignment=System.Windows.HorizontalAlignment.Right;
            info.TextAlignment=TextAlignment.Right;
            top.Children.Add(info);
            grid.IsReadOnly=true;
            grid.SelectionMode=DataGridSelectionMode.Extended;
            grid.SelectionUnit=DataGridSelectionUnit.FullRow;
            grid.EnableRowVirtualization=true;
            grid.EnableColumnVirtualization=true;
            root.Children.Add(grid);
            Loaded+=(s,e)=>Refresh();
        }
        private async void Refresh()
        {
            int id=++requestId;
            string batch=batchBox.Text.Trim(),part=partBox.Text.Trim();
            int currentOffset=offset;
            info.Text="Archive 조회 중...";
            previous.IsEnabled=false;next.IsEnabled=false;
            try
            {
                var result=await System.Threading.Tasks.Task.Run(()=>{
                    var counts=TestResultArchiveStore.CountMatches(batch,part,from,to);
                    var table=TestResultArchiveStore.Search(batch,part,from,to,currentOffset,PageSize);
                    return (counts,table);
                });
                if(id!=requestId)return;
                total=result.counts.rows;
                grid.ItemsSource=result.table.DefaultView;
                previous.IsEnabled=offset>0;
                next.IsEnabled=offset+result.table.Rows.Count<total;
                info.Text=$"전체 {total:N0}건 / Batch {result.counts.batches:N0}개 · {Math.Min(offset+1,total):N0}~{offset+result.table.Rows.Count:N0}건 표시";
            }
            catch(Exception ex)
            {
                if(id!=requestId)return;
                info.Text="Archive 조회 실패";
                MessageBox.Show("Archive 조회 오류: "+ex.Message,"오류",MessageBoxButton.OK,MessageBoxImage.Error);
            }
        }
        private async void RestoreSelected()
        {
            var keys=grid.SelectedItems.OfType<DataRowView>()
                .Select(r=>(batch:Convert.ToString(r["batch"])??"",no:Convert.ToString(r["no"])??""))
                .ToList();
            if(keys.Count==0){MessageBox.Show("복원할 행을 선택해 주세요.");return;}
            if(MessageBox.Show($"선택한 {keys.Count}행을 복원하시겠습니까?","Archive 복원",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            try
            {
                info.Text="선택 행 복원 중...";
                int count=await System.Threading.Tasks.Task.Run(()=>TestResultArchiveStore.Restore(keys));
                Restored=true;
                Refresh();
            }
            catch(Exception ex){MessageBox.Show("복원 실패: "+ex.Message,"오류",MessageBoxButton.OK,MessageBoxImage.Error);}
        }
    }
    internal sealed class TestResultArchiveMoveWindow : Window
    {
        private const int PageSize = 500;
        private readonly DataGrid grid = new DataGrid { IsReadOnly=true, AutoGenerateColumns=true, SelectionMode=DataGridSelectionMode.Extended, SelectionUnit=DataGridSelectionUnit.FullRow };
        private readonly DatePicker regFrom = new DatePicker { Width=130 };
        private readonly DatePicker regTo = new DatePicker { SelectedDate=DateTime.Today,Width=130 };
        private readonly DatePicker updFrom = new DatePicker { Width=130 };
        private readonly DatePicker updTo = new DatePicker { SelectedDate=DateTime.Today,Width=130 };
        private readonly DatePicker mfFrom = new DatePicker { Width=130 };
        private readonly DatePicker mfTo = new DatePicker { SelectedDate=DateTime.Today,Width=130 };
        private readonly System.Windows.Controls.CheckBox reg = new System.Windows.Controls.CheckBox { Content="등록일",IsChecked=true };
        private readonly System.Windows.Controls.CheckBox upd = new System.Windows.Controls.CheckBox { Content="수정일",IsChecked=true };
        private readonly System.Windows.Controls.CheckBox mf = new System.Windows.Controls.CheckBox { Content="제조일자",IsChecked=true };
        private readonly TextBlock info = new TextBlock { VerticalAlignment=VerticalAlignment.Center };
        private readonly Button search = new Button { Content="대상 조회",Width=90,Height=28 };
        private readonly Button move = new Button { Content="선택 Batch 이관",Width=125,Height=28,IsEnabled=false };
        private readonly Button previous = new Button { Content="이전",Width=65,Height=28 };
        private readonly Button next = new Button { Content="다음",Width=65,Height=28 };
        private int offset,requestId;
        private long totalBatches,totalRows;
        private TestResultArchiveStore.DateRanges? lastRanges;
        private bool loaded;
        public bool MovedAnything {get;private set;}
        public TestResultArchiveMoveWindow()
        {
            Title="Test Result Archive 대상 선택 이관";Width=1150;Height=720;WindowStartupLocation=WindowStartupLocation.CenterOwner;
            var root=new DockPanel { Margin=new Thickness(12) };Content=root;
            var top=new StackPanel { Margin=new Thickness(0,0,0,10) };DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
            top.Children.Add(new TextBlock { Text="선택한 날짜 조건을 모든 행이 만족하는 Batch만 표시합니다. Batch 전체 단위로 이관됩니다.",Margin=new Thickness(0,0,0,8) });
            var controls=new StackPanel { Orientation=Orientation.Vertical };
            foreach (var item in new[] {
                (check:reg,from:regFrom,to:regTo),
                (check:upd,from:updFrom,to:updTo),
                (check:mf,from:mfFrom,to:mfTo) })
            {
                var line = new StackPanel { Orientation=Orientation.Horizontal, Margin=new Thickness(0,2,0,2) };
                item.check.Width=105;
                item.check.VerticalAlignment=VerticalAlignment.Center;
                line.Children.Add(item.check);
                line.Children.Add(item.from);
                line.Children.Add(new TextBlock { Text="  ~  ",VerticalAlignment=VerticalAlignment.Center });
                line.Children.Add(item.to);
                TestResultDateInput.Attach(item.from);
                TestResultDateInput.Attach(item.to);
                controls.Children.Add(line);
            }
            var actions=new StackPanel { Orientation=Orientation.Horizontal, Margin=new Thickness(0,6,0,0) };
            foreach(var b in new[]{search,previous,next,move}) { b.Margin=new Thickness(0,0,10,0);actions.Children.Add(b); }
            controls.Children.Add(actions);
            top.Children.Add(controls);
            info.Margin=new Thickness(0,8,0,0);top.Children.Add(info);
            root.Children.Add(grid);
            previous.IsEnabled=false;next.IsEnabled=false;
            search.Click+=(s,e)=>{offset=0;Refresh(true);};
            previous.Click+=(s,e)=>{offset=Math.Max(0,offset-PageSize);Refresh(false);};
            next.Click+=(s,e)=>{offset+=PageSize;Refresh(false);};
            grid.SelectionChanged+=(s,e)=>UpdateSelection();
            move.Click+=(s,e)=>MoveSelected();
        }
        private void UpdateSelection()
        {
            int count=grid.SelectedItems.OfType<DataRowView>().Count();
            long rows=grid.SelectedItems.OfType<DataRowView>().Sum(x=>long.TryParse(Convert.ToString(x["대상 건수"]),out var n)?n:0);
            move.IsEnabled=loaded && count>0;
            if(loaded) info.Text=$"대상 전체 {totalBatches:N0} Batch / {totalRows:N0}행 · 페이지 {offset+1:N0}~{Math.Min(offset+grid.Items.Count,totalBatches):N0} Batch · 선택 {count:N0} Batch / {rows:N0}행";
        }
        private async void Refresh(bool newCriteria)
        {
            if(newCriteria)
            {
                if(reg.IsChecked!=true && upd.IsChecked!=true && mf.IsChecked!=true){MessageBox.Show("날짜 조건을 하나 이상 선택해 주세요.");return;}
                foreach(var item in new[] { (check:reg,from:regFrom,to:regTo), (check:upd,from:updFrom,to:updTo), (check:mf,from:mfFrom,to:mfTo) })
                {
                    if(item.check.IsChecked!=true) continue;
                    if(!TestResultDateInput.NormalizeDatePicker(item.from) || !TestResultDateInput.NormalizeDatePicker(item.to))
                    { MessageBox.Show("날짜 입력값이 올바르지 않습니다."); return; }
                    if(!item.from.SelectedDate.HasValue && !item.to.SelectedDate.HasValue)
                    { MessageBox.Show("체크한 날짜 조건에 From 또는 To를 입력해 주세요."); return; }
                    if(item.from.SelectedDate.HasValue && item.to.SelectedDate.HasValue && item.from.SelectedDate.Value > item.to.SelectedDate.Value)
                    { MessageBox.Show("From 날짜가 To 날짜보다 늦습니다."); return; }
                }
                lastRanges = new TestResultArchiveStore.DateRanges {
                    Reg=reg.IsChecked==true, RegFrom=regFrom.SelectedDate, RegTo=regTo.SelectedDate,
                    Upd=upd.IsChecked==true, UpdFrom=updFrom.SelectedDate, UpdTo=updTo.SelectedDate,
                    Mf=mf.IsChecked==true, MfFrom=mfFrom.SelectedDate, MfTo=mfTo.SelectedDate
                };
            }
            int id=++requestId,currentOffset=offset;
            loaded=false;search.IsEnabled=false;move.IsEnabled=false;previous.IsEnabled=false;next.IsEnabled=false;
            info.Text="Archive 이관 대상 조회 중...";
            try
            {
                var result=await System.Threading.Tasks.Task.Run(()=>{
                    var count=TestResultArchiveStore.Preview(lastRanges!);
                    var table=TestResultArchiveStore.EligibleBatches(lastRanges!,currentOffset,PageSize);
                    return (count,table);
                });
                if(id!=requestId)return;
                totalBatches=result.count.batches;totalRows=result.count.records;
                grid.ItemsSource=result.table.DefaultView;loaded=true;
                previous.IsEnabled=offset>0;next.IsEnabled=offset+result.table.Rows.Count<totalBatches;
                UpdateSelection();
            }
            catch(Exception ex){if(id==requestId)MessageBox.Show("대상 조회 오류: "+ex.Message,"오류",MessageBoxButton.OK,MessageBoxImage.Error);}
            finally{if(id==requestId)search.IsEnabled=true;}
        }
        private async void MoveSelected()
        {
            var keys=grid.SelectedItems.OfType<DataRowView>().Select(x=>Convert.ToString(x["Batch No"])??"").Where(x=>x.Length>0).Distinct().ToList();
            if(keys.Count==0)return;
            long rows=grid.SelectedItems.OfType<DataRowView>().Sum(x=>long.TryParse(Convert.ToString(x["대상 건수"]),out var n)?n:0);
            if(MessageBox.Show($"선택한 {keys.Count:N0}개 Batch / {rows:N0}행을 Archive로 이관하시겠습니까?","선택 Batch 이관",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            move.IsEnabled=false;search.IsEnabled=false;info.Text="선택한 Batch 이관 중...";
            try
            {
                var result=await System.Threading.Tasks.Task.Run(()=>TestResultArchiveStore.MoveSelectedBatches(keys,lastRanges!));
                MovedAnything=true;
                MessageBox.Show($"{result.batches:N0}개 Batch / {result.records:N0}행 이관 완료","Archive 이관",MessageBoxButton.OK,MessageBoxImage.Information);
                offset=0;Refresh(false);
            }
            catch(Exception ex){MessageBox.Show("Archive 이관 오류: "+ex.Message,"오류",MessageBoxButton.OK,MessageBoxImage.Error);search.IsEnabled=true;UpdateSelection();}
        }
    }

}
