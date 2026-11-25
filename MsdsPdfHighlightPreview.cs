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
            => TryCreate(input,output,pageNumber,source,out _);
        public static bool TryCreate(string input, string output, int pageNumber, string source, out double top)
        {
            top=0;
            using var parsed=UglyToad.PdfPig.PdfDocument.Open(input);
            if(pageNumber<1 || pageNumber>parsed.NumberOfPages)return false;
            var page=parsed.GetPage(pageNumber);
            var hits=FindHits(page,source);
            return hits.Count>0 && Draw(input,output,pageNumber,page,hits,out top);
        }
        private static List<Letter> FindHits(Page page,string source)
        {
            var target=Compact(source);
            if(target.Length<2)return new List<Letter>();
            var ordered=page.Letters.GroupBy(l=>Math.Round(l.GlyphRectangle.Bottom/3.0)*3)
                .OrderByDescending(g=>g.Key).SelectMany(g=>g.OrderBy(l=>l.GlyphRectangle.Left));
            foreach(var sequence in new IEnumerable<Letter>[] {page.Letters,ordered})
            {
                var letters=new List<Letter>(); var text=new System.Text.StringBuilder();
                foreach(var letter in sequence) foreach(var ch in Compact(letter.Value)) {text.Append(ch);letters.Add(letter);}
                var all=text.ToString(); int start=all.IndexOf(target,StringComparison.Ordinal);
                if(start>=0 && all.IndexOf(target,start+1,StringComparison.Ordinal)<0)
                    return letters.Skip(start).Take(target.Length).Distinct().ToList();
            }
            // Multi-line evidence: require every nonempty line to match uniquely.
            var parts=source.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Where(x=>Compact(x).Length>1).ToList();
            if(parts.Count>1)
            {
                var hits=new List<Letter>();
                foreach(var part in parts) {var found=FindHits(page,part);if(found.Count==0)return new List<Letter>();hits.AddRange(found);}
                return hits.Distinct().ToList();
            }
            return new List<Letter>();
        }
        internal static bool TryCreateMany(string input,string output,IEnumerable<(int Page,string Source)> targets)
        {
            using var parsed=UglyToad.PdfPig.PdfDocument.Open(input);
            using var doc=PdfReader.Open(input,PdfDocumentOpenMode.Modify);
            bool any=false;
            foreach(var group in targets.GroupBy(t=>t.Page))
            {
                if(group.Key<1 || group.Key>parsed.NumberOfPages)continue;
                var page=parsed.GetPage(group.Key);
                var hits=group.SelectMany(t=>FindHits(page,t.Source)).Distinct().ToList();
                if(hits.Count>0 && DrawPage(doc.Pages[group.Key-1],page,hits,out _))any=true;
            }
            if(any)doc.Save(output);
            return any;
        }
        private static bool Draw(string input,string output,int pageNumber,Page page,List<Letter> hits,out double top)
        {
            using var doc=PdfReader.Open(input,PdfDocumentOpenMode.Modify);
            if(!DrawPage(doc.Pages[pageNumber-1],page,hits,out top))return false;
            doc.Save(output);return true;
        }
        private static bool DrawPage(PdfSharp.Pdf.PdfPage pdfPage,Page page,List<Letter> hits,out double top)
        {
            top=0;
            if(hits.Count==0 || pdfPage.Rotate!=0 || Math.Abs(pdfPage.Width.Point-(double)page.Width)>1 || Math.Abs(pdfPage.Height.Point-(double)page.Height)>1)return false;
            if(hits.Any(l=>l.GlyphRectangle.Left<0 || l.GlyphRectangle.Bottom<0 || l.GlyphRectangle.Right>pdfPage.Width.Point || l.GlyphRectangle.Top>pdfPage.Height.Point))return false;
            using(var gfx=XGraphics.FromPdfPage(pdfPage,XGraphicsPdfPageOptions.Append))
            {
                var brush=new XSolidBrush(XColor.FromArgb(150,255,190,0));
                foreach(var line in hits.GroupBy(l=>Math.Round(l.GlyphRectangle.Bottom/3.0)*3))
                {
                    var sorted=line.OrderBy(l=>l.GlyphRectangle.Left).ToList();
                    double left=0,right=0,bottom=0,upper=0;bool active=false;
                    foreach(var l in sorted)
                    {
                        var r=l.GlyphRectangle;
                        if(r.Width<=0 || r.Height<=0)continue;
                        if(active && r.Left>right+5) {gfx.DrawRectangle(brush,left,pdfPage.Height.Point-upper,right-left,upper-bottom);active=false;}
                        if(!active){left=r.Left;right=r.Right;bottom=r.Bottom;upper=r.Top;active=true;}
                        else {right=Math.Max(right,r.Right);bottom=Math.Min(bottom,r.Bottom);upper=Math.Max(upper,r.Top);}
                    }
                    if(active)gfx.DrawRectangle(brush,left,pdfPage.Height.Point-upper,right-left,upper-bottom);
                }
            }
            top=hits.Min(l=>pdfPage.Height.Point-l.GlyphRectangle.Top);return true;
        }
        internal static bool IsIngredientField(string field) => new[]{"twa","stel","oral_ld50","dermal_ld50","inhalation_lc50","inhal_lc50"}.Contains(field,StringComparer.OrdinalIgnoreCase);
        internal static bool TryIngredient(string input,string output,string field,string cas,string ingredient,string before,out int pageNumber,out double top)
        {
            pageNumber=0; top=0;
            bool exposure=field.Equals("twa",StringComparison.OrdinalIgnoreCase)||field.Equals("stel",StringComparison.OrdinalIgnoreCase);
            int section=exposure?8:11, current=0;
            var matches=new List<(int Page,List<Letter> Letters)>();
            using var parsed=UglyToad.PdfPig.PdfDocument.Open(input);
            for(int i=1;i<=parsed.NumberOfPages;i++)
            {
                var page=parsed.GetPage(i);
                var lines=page.Letters.GroupBy(l=>Math.Round(l.GlyphRectangle.Bottom/3.0)*3).OrderByDescending(g=>g.Key).Select(g=>g.OrderBy(l=>l.GlyphRectangle.Left).ToList()).ToList();
                double ownerX=0;
                bool owner=false;
                List<Letter>? ownerLetters=null;
                foreach(var letters in lines)
                {
                    string line=string.Concat(letters.Select(l=>l.Value));
                    string t=Compact(line);
                    var heading=System.Text.RegularExpressions.Regex.Match(t,@"^(?:제)?(\d{1,2})[.항](?:노출방지|독성에관한|환경에미치는|물리화학적|안정성|응급조치|구성성분|취급및저장|폐기|운송|법적규제|그밖의|폭발|누출|유해성|화학제품)");
                    if(heading.Success) { current=int.Parse(heading.Groups[1].Value); owner=false; }
                    if(current!=section)continue;
                    if(pageNumber==0)pageNumber=i;
                    bool named=(!string.IsNullOrWhiteSpace(cas)&&t.Contains(Compact(cas),StringComparison.Ordinal))||(!string.IsNullOrWhiteSpace(ingredient)&&t.Contains(Compact(ingredient),StringComparison.Ordinal));
                    if(named) { owner=true; ownerX=letters[0].GlyphRectangle.Left; ownerLetters=letters; }
                    else if(owner && letters[0].GlyphRectangle.Left<=ownerX+12)owner=false;
                    else if(System.Text.RegularExpressions.Regex.IsMatch(t,@"\d{2,7}-\d{2}-\d"))owner=false;
                    if(!owner)continue;
                    var value=System.Text.RegularExpressions.Regex.Match(before,@"\d+(?:[.,]\d+)?\s*(?:ppm|mg\s*/\s*(?:m[³3]|kg|l))",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if(!value.Success||!System.Text.RegularExpressions.Regex.IsMatch(t, @"(?<![\d.,])"+System.Text.RegularExpressions.Regex.Escape(Compact(value.Value))+@"(?![\dA-Z])"))continue;
                    if(exposure&&!t.Contains(field.ToUpperInvariant(),StringComparison.Ordinal))continue;
                    if(!exposure)
                    {
                        string route = field.Equals("oral_ld50",StringComparison.OrdinalIgnoreCase) ? @"경구|ORAL" : field.Equals("dermal_ld50",StringComparison.OrdinalIgnoreCase) ? @"경피|DERMAL" : @"흡입|INHAL";
                        if(!System.Text.RegularExpressions.Regex.IsMatch(t,route))continue;
                    }
                    if(ownerLetters!=null) matches.Add((i,ownerLetters.Concat(letters).Distinct().ToList()));
                }
            }
            if(matches.Count!=1)return false;
            pageNumber=matches[0].Page;
            return Draw(input,output,pageNumber,parsed.GetPage(pageNumber),matches[0].Letters,out top);
        }
        private static string Compact(string value) => new string(value.Where(c=>!char.IsWhiteSpace(c)).Select(c=>c is '·' or '∙' or '•' ? '·' : char.ToUpperInvariant(c)).ToArray());
    }
}
