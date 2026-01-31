using System.Collections.Generic;
using System.Text;
using UglyToad.PdfPig;

namespace pdf_chat_app.Services
{
    public static class PdfTextService
    {
        public static List<PageText> ReadAllPages(string pdfPath)
        {
            var pages = new List<PageText>();

            using (var doc = PdfDocument.Open(pdfPath))
            {
                int i = 1;
                foreach (var page in doc.GetPages())
                {
                    var text = page.Text ?? "";
                    pages.Add(new PageText { PageNumber = i, Text = text });
                    i++;
                }
            }
            return pages;
        }
    }

    public class PageText
    {
        public int PageNumber { get; set; }
        public string Text { get; set; }
    }
}
