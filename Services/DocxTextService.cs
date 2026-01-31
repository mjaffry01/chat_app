using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using pdf_chat_app.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Documents;

namespace pdf_chat_app.Services
{
    public static class DocxTextService
    {
        // Reads a .docx and returns "chunks" as PageText (we reuse your model)
        public static List<PageText> ReadAllPages(string docxPath, int maxCharsPerChunk = 2500)
        {
            if (string.IsNullOrWhiteSpace(docxPath))
                throw new ArgumentException("docxPath is empty.");

            var allText = ExtractText(docxPath);
            if (string.IsNullOrWhiteSpace(allText))
                return new List<PageText>();

            return ChunkToPages(allText, maxCharsPerChunk);
        }

        private static string ExtractText(string docxPath)
        {
            var sb = new StringBuilder();

            using (var doc = WordprocessingDocument.Open(docxPath, false))
            {
                var body = doc.MainDocumentPart?.Document?.Body;
                if (body == null) return "";

                foreach (var para in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())

                {
                    var line = para.InnerText ?? "";
                    line = line.Trim();
                    if (line.Length == 0) continue;

                    sb.AppendLine(line);
                }
            }

            return sb.ToString();
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

                // try to cut on a boundary
                int cut = chunk.LastIndexOf("\n", StringComparison.Ordinal);
                if (cut > 400) // avoid super tiny last chunk
                {
                    chunk = chunk.Substring(0, cut).Trim();
                    take = cut;
                }

                pages.Add(new PageText
                {
                    PageNumber = pageNo++,
                    Text = chunk
                });

                i += Math.Max(1, take);
            }

            return pages;
        }
    }
}
