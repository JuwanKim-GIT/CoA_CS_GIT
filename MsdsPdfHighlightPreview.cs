using System;
using System.Collections.Generic;
using System.Linq;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.Content;

namespace QCS
{
    // Only a temporary preview is modified; the registered PDF is never written.
    internal static class MsdsPdfHighlightPreview
    {
        public static bool TryCreate(string input, string output, int pageNumber, string source)
        {
            var target = Compact(source);
            if (target.Length < 2 || pageNumber < 1) return false;
            using var parsed = UglyToad.PdfPig.PdfDocument.Open(input);
            if (pageNumber > parsed.NumberOfPages) return false;
            var page = parsed.GetPage(pageNumber);
            var letters = new List<Letter>();
            var text = new System.Text.StringBuilder();
            foreach (var letter in page.Letters)
            {
                foreach (var ch in Compact(letter.Value)) { text.Append(ch); letters.Add(letter); }
            }
            var all = text.ToString();
            int start = all.IndexOf(target, StringComparison.Ordinal);
            if (start < 0 || all.IndexOf(target,start+1,StringComparison.Ordinal)>=0) return false;
            var hits = letters.Skip(start).Take(target.Length).Distinct().ToList();
            using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
            var pdfPage = doc.Pages[pageNumber-1];
            if (pdfPage.Rotate != 0 || Math.Abs(pdfPage.Width.Point-(double)page.Width)>1 || Math.Abs(pdfPage.Height.Point-(double)page.Height)>1) return false;
            // Reject coordinates outside page bounds (e.g. unusual crop boxes).
            if (hits.Any(l=>l.GlyphRectangle.Left<0 || l.GlyphRectangle.Bottom<0 || l.GlyphRectangle.Right>pdfPage.Width.Point || l.GlyphRectangle.Top>pdfPage.Height.Point)) return false;
            using (var gfx=XGraphics.FromPdfPage(pdfPage,XGraphicsPdfPageOptions.Append))
            {
                var brush=new XSolidBrush(XColor.FromArgb(95,255,235,0));
                foreach(var l in hits)
                {
                    var r=l.GlyphRectangle;
                    if(r.Width>0 && r.Height>0) gfx.DrawRectangle(brush,r.Left,pdfPage.Height.Point-r.Top,r.Width,r.Height);
                }
            }
            doc.Save(output);
            return true;
        }
        private static string Compact(string value) => new string(value.Where(c=>!char.IsWhiteSpace(c)).Select(c=>c is '·' or '∙' or '•' ? '·' : char.ToUpperInvariant(c)).ToArray());
    }
}
