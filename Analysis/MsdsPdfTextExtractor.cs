using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace QCS.Analysis
{
    public sealed class MsdsPdfPageText
    {
        public int PageNumber { get; set; }
        public string Text { get; set; } = "";
    }

    public sealed class MsdsPdfTextResult
    {
        public string FilePath { get; set; } = "";
        public int PageCount { get; set; }
        public List<MsdsPdfPageText> Pages { get; } = new();
        public string FullText => string.Join(Environment.NewLine + Environment.NewLine,
            Pages.Select(p => $"[PAGE {p.PageNumber}]" + Environment.NewLine + p.Text));
        public bool HasExtractableText => Pages.Any(p => !string.IsNullOrWhiteSpace(p.Text));
        public bool RequiresManualEntry => !HasExtractableText;
    }

    /// <summary>
    /// Reads embedded PDF text locally. No OCR and no external network transmission.
    /// Does not save anything to the QCS database.
    /// </summary>
    public sealed class MsdsPdfTextExtractor
    {
        public Task<MsdsPdfTextResult> ExtractAsync(
            string pdfPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pdfPath))
                throw new ArgumentException("PDF 경로가 비어 있습니다.", nameof(pdfPath));
            if (!File.Exists(pdfPath))
                throw new FileNotFoundException("PDF 파일을 찾을 수 없습니다.", pdfPath);
            if (!string.Equals(Path.GetExtension(pdfPath), ".pdf", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("PDF 파일만 처리할 수 있습니다.", nameof(pdfPath));

            // PdfPig is synchronous. Run outside the WPF UI thread.
            return Task.Run(() => ExtractCore(pdfPath, cancellationToken), cancellationToken);
        }

        private static MsdsPdfTextResult ExtractCore(string pdfPath, CancellationToken cancellationToken)
        {
            var result = new MsdsPdfTextResult { FilePath = pdfPath };
            using (var document = PdfDocument.Open(pdfPath))
            {
                result.PageCount = document.NumberOfPages;
                foreach (var page in document.GetPages())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Layout-aware text extraction is more useful for MSDS tables than raw letter order.
                    var text = ContentOrderTextExtractor.GetText(page);
                    result.Pages.Add(new MsdsPdfPageText
                    {
                        PageNumber = page.Number,
                        Text = text ?? ""
                    });
                }
            }
            return result;
        }
    }
}
