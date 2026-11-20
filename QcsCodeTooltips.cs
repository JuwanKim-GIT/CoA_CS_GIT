using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace QCS;

public sealed class QcsCodeTooltips
{
    private Dictionary<(string Group, string Value), string> _entries = new();
    public void Reload(SqliteConnection connection)
    {
        var entries = new Dictionary<(string, string), string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT zx_code_fldname,zx_code_value,zx_code_desc1,zx_code_desc2,zx_code_cmmt FROM zx_code_mstr";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string Text(int i) => reader.IsDBNull(i) ? "" : reader.GetString(i);
            string OneLine(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            string comment = OneLine(Text(4));
            if (comment.Length > 80) comment = comment.Substring(0, 80).TrimEnd() + "...";
            string tooltip = string.Join(" | ", new[] { OneLine(Text(2)), OneLine(Text(3)), comment }.Where(v => v.Length > 0));
            if (tooltip.Length > 0) entries[(Text(0), Text(1))] = tooltip;
        }
        _entries = entries;
    }
    public string? Get(string group, string value) => _entries.TryGetValue((group, value), out var text) ? text : null;
}
