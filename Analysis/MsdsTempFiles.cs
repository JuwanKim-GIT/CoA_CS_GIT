using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QCS.Analysis
{
    public static class MsdsTempFiles
    {
        public static string Folder => Path.Combine(AppContext.BaseDirectory, "TempFiles");
        private static string Prefix(string path)
        {
            var full = Path.GetFullPath(path).ToUpperInvariant();
            using var sha = SHA256.Create();
            var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(full))).Replace("-", "").Substring(0, 12);
            var stem = Path.GetFileNameWithoutExtension(path);
            foreach (var ch in Path.GetInvalidFileNameChars()) stem = stem.Replace(ch, '_');
            return stem + "_" + hash;
        }
        public static void Save(string pdfPath, MsdsPdfTextResult text, MsdsAnalysisResult parsed)
        {
            Directory.CreateDirectory(Folder);
            var prefix = Path.Combine(Folder, Prefix(pdfPath));
            WriteAtomic(prefix + "_text.txt", text.FullText);
            var options = new JsonSerializerOptions { WriteIndented = true };
            var snapshot = new {
                PdfPath = pdfPath,
                SavedAt = DateTimeOffset.Now,
                Fields = parsed.Fields,
                Ingredients = parsed.Ingredients,
                Warnings = parsed.Warnings,
                parsed.RequiresManualEntry
            };
            WriteAtomic(prefix + "_parsed.json", JsonSerializer.Serialize(snapshot, options));
            var debug = new {
                PdfPath = pdfPath,
                RuleFile = MsdsRuleLoader.RulePath,
                TextPageCount = text.PageCount,
                TextLength = text.FullText.Length,
                FieldMatches = parsed.Fields.Select(f => new { f.FieldKey, f.Value, f.SourcePage, f.SourceText, f.Status, f.Confidence }).ToArray(),
                Notes = "SourceText는 추출 근거 원문입니다. 규칙 패턴 ID 추적은 추후 확장 대상입니다."
            };
            WriteAtomic(prefix + "_debug.json", JsonSerializer.Serialize(debug, options));
        }
        public static void DeleteAfterCommitted(string pdfPath)
        {
            var prefix = Prefix(pdfPath) + "_";
            if (!Directory.Exists(Folder)) return;
            foreach (var file in Directory.EnumerateFiles(Folder, prefix + "*")) File.Delete(file);
        }
        private static void WriteAtomic(string path, string content)
        {
            var tmp = path + ".writing";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            File.Move(tmp, path, true);
        }
    }
}
