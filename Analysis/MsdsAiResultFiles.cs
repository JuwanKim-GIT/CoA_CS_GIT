using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QCS.Analysis
{
    public static class MsdsAiResultFiles
    {
        public static void Save(string pdfPath, MsdsAnalysisResult result)
        {
            Directory.CreateDirectory(MsdsTempFiles.Folder);
            string full = Path.GetFullPath(pdfPath).ToUpperInvariant();
            using var sha = SHA256.Create();
            string hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(full))).Substring(0, 12);
            string stem = Path.GetFileNameWithoutExtension(pdfPath);
            foreach (char c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
            string path = Path.Combine(MsdsTempFiles.Folder, stem + "_" + hash + "_ai_pdf_parsed.json");
            var data = new { PdfPath = pdfPath, SavedAt = DateTimeOffset.Now, Engine = "AiDirect",
                result.Fields, result.Ingredients, result.Warnings, result.RequiresManualEntry };
            string temp = path + ".writing";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
    }
}
