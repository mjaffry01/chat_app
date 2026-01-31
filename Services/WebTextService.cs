using pdf_chat_app.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace pdf_chat_app.Services
{
    public static class WebTextService
    {
        private static readonly HttpClient _http = new HttpClient();

        public static async Task<List<PageText>> LoadChunksAsync(string url, int maxCharsPerChunk = 2500)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL is empty.");

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                throw new ArgumentException("URL is invalid.");

            string html = await _http.GetStringAsync(uri);
            string text = HtmlToText(html);

            if (string.IsNullOrWhiteSpace(text))
                return new List<PageText>();

            return ChunkToPages(text, maxCharsPerChunk);
        }

        private static string HtmlToText(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";

            // remove scripts/styles
            html = Regex.Replace(html, "<script[\\s\\S]*?</script>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<style[\\s\\S]*?</style>", "", RegexOptions.IgnoreCase);

            // replace <br> and </p> with newlines
            html = Regex.Replace(html, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "</p>", "\n", RegexOptions.IgnoreCase);

            // strip tags
            string text = Regex.Replace(html, "<[^>]+>", " ");

            // decode a few common entities (keep it lightweight)
            text = text.Replace("&nbsp;", " ")
                       .Replace("&amp;", "&")
                       .Replace("&lt;", "<")
                       .Replace("&gt;", ">")
                       .Replace("&quot;", "\"")
                       .Replace("&#39;", "'");

            // normalize whitespace
            text = Regex.Replace(text, "[ \t]+", " ");
            text = Regex.Replace(text, "\n{3,}", "\n\n");

            return text.Trim();
        }

        private static List<PageText> ChunkToPages(string text, int maxChars)
        {
            var pages = new List<PageText>();
            var clean = text.Replace("\r", "").Trim();

            int pageNo = 1;
            int i = 0;

            while (i < clean.Length)
            {
                int take = Math.Min(maxChars, clean.Length - i);
                string chunk = clean.Substring(i, take);

                int cut = chunk.LastIndexOf("\n", StringComparison.Ordinal);
                if (cut > 400)
                {
                    chunk = chunk.Substring(0, cut).Trim();
                    take = cut;
                }

                pages.Add(new PageText { PageNumber = pageNo++, Text = chunk });

                i += Math.Max(1, take);
            }

            return pages;
        }
    }
}
