using System;
using System.Linq;
using QCS.Analysis;
namespace QCS
{
    internal static class MsdsIngredientLearningKey
    {
        internal static readonly string[] Fields = { "Twa", "Stel", "OralLd50", "DermalLd50", "InhalationLc50" };
        internal static string Key(string cas, string field) => "ING:" + cas.Trim() + ":" + field;
        internal static bool Parse(string key, out string cas, out string field)
        {
            var parts = key.Split(':'); cas = parts.Length == 3 ? parts[1] : ""; field = parts.Length == 3 ? parts[2] : "";
            return parts.Length == 3 && parts[0] == "ING" && System.Text.RegularExpressions.Regex.IsMatch(cas, @"^\d{2,7}-\d{2}-\d$") && Fields.Contains(field);
        }
        internal static string? Get(object row, string field) => row.GetType().GetProperty(field)?.GetValue(row) as string;
        internal static void Set(object row, string field, string? value) => row.GetType().GetProperty(field)?.SetValue(row, value ?? "");
    }
}
