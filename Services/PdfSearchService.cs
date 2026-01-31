using System;
using System.Collections.Generic;
using System.Linq;

namespace pdf_chat_app.Services
{
    public static class PdfSearchService
    {
        public static List<SearchHit> Search(List<PageText> pages, string query, int top = 5)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0) return new List<SearchHit>();

            var terms = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(t => t.Trim().ToLowerInvariant())
                             .Where(t => t.Length > 1)
                             .ToList();

            var hits = new List<SearchHit>();

            foreach (var p in pages)
            {
                var text = (p.Text ?? "").ToLowerInvariant();
                if (text.Length == 0) continue;

                int score = 0;
                for (int i = 0; i < terms.Count; i++)
                {
                    if (text.Contains(terms[i])) score++;
                }

                if (score > 0)
                {
                    hits.Add(new SearchHit
                    {
                        PageNumber = p.PageNumber,
                        Score = score,
                        Snippet = MakeSnippet(p.Text, terms)
                    });
                }
            }

            return hits.OrderByDescending(h => h.Score).ThenBy(h => h.PageNumber).Take(top).ToList();
        }

        private static string MakeSnippet(string pageText, List<string> terms)
        {
            if (string.IsNullOrWhiteSpace(pageText)) return "";

            var lower = pageText.ToLowerInvariant();
            int idx = -1;

            foreach (var t in terms)
            {
                idx = lower.IndexOf(t);
                if (idx >= 0) break;
            }

            if (idx < 0) idx = 0;

            int start = Math.Max(0, idx - 80);
            int len = Math.Min(pageText.Length - start, 240);

            var snippet = pageText.Substring(start, len).Replace("\r", " ").Replace("\n", " ");
            return snippet + (start + len < pageText.Length ? " ..." : "");
        }
    }

    public class SearchHit
    {
        public int PageNumber { get; set; }
        public int Score { get; set; }
        public string Snippet { get; set; }
    }
}
