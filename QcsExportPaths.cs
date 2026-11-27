using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace QCS;

internal static class QcsExportPaths
{
    private static (string? Network, string Local) Read(string group, string key, string localDefault)
    {
        string? network = null;
        string local = localDefault;
        using var connection = new SqliteConnection(DatabaseManager.GetConnectionString("common"));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT zx_code_value, zx_code_cmmt FROM zx_code_mstr
                               WHERE zx_code_fldname = $group AND zx_code_value IN ($network, $local)";
        command.Parameters.AddWithValue("$group", group);
        command.Parameters.AddWithValue("$network", key + "_N");
        command.Parameters.AddWithValue("$local", key + "_L");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string? path = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
            if (string.IsNullOrEmpty(path)) continue;
            if (reader.GetString(0) == key + "_N") network = path;
            else local = path;
        }
        return (network, Path.GetFullPath(local, AppContext.BaseDirectory));
    }

    internal static (string Path, bool IsLocal) Template(string key, string file, bool allowLocal = true)
    {
        var paths = Read("TEMPLATE_PATH", key, "QCS_xltm");
        if (paths.Network != null)
        {
            string networkFile = Path.Combine(paths.Network, file);
            try
            {
                using var stream = File.Open(networkFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return (networkFile, false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        if (!allowLocal)
            throw new IOException($"서버 템플릿을 열 수 없습니다: {file}");

        string localFile = Path.Combine(paths.Local, file);
        using var localStream = File.Open(localFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return (localFile, true);
    }

    internal static (string? Network, string Local) Output(string key) =>
        Read("OUTPUT_PATH", key, Path.Combine("OUTPUT", key));

    internal static (string Path, bool IsLocal) Save(string key, string file, Action<string> write, bool allowLocal = true)
    {
        var paths = Output(key);
        if (paths.Network != null)
        {
            try { return (WriteFile(paths.Network, file, write), false); }
            catch (Exception ex) when (allowLocal && (ex is IOException or UnauthorizedAccessException)) { }
        }
        if (!allowLocal)
            throw new IOException($"Server output path is not configured: {key}");
        return (WriteFile(paths.Local, file, write), true);
    }

    private static string WriteFile(string directory, string file, Action<string> write)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, file);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + Path.GetExtension(file));
        try
        {
            write(temporary);
            File.Move(temporary, path);
            return path;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

