using pdf_chat_app.Infrastructure;
using pdf_chat_app.Models;
using pdf_chat_app.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;
using System.Windows.Input;

namespace pdf_chat_app.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }

        // =========================
        // Source model
        // =========================
        public enum SourceKind
        {
            Pdf = 0,
            Word = 1,
            Web = 2
        }

        private SourceKind _activeSource = SourceKind.Pdf;
        public SourceKind ActiveSource
        {
            get { return _activeSource; }
            set
            {
                if (_activeSource == value) return;
                _activeSource = value;
                OnPropertyChanged();
            }
        }

        // =========================
        // State
        // =========================
        private string _lastUserQuestion = "";
        private string _currentInput;

        private string _selectedPdfPath;
        private string _selectedWordPath;
        private string _websiteUrl;

        // Cached “pages/chunks” for whichever source is active
        private List<PageText> _pages = new List<PageText>();

        // Vocab built from loaded content (for typo-tolerant correction)
        private HashSet<string> _docVocab = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Synonyms (free API)
        private static readonly HttpClient _http = new HttpClient();
        private readonly Dictionary<string, List<string>> _synCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // =========================
        // Bindings
        // =========================
        public ObservableCollection<ChatMessage> Messages { get; private set; }

        public string CurrentInput
        {
            get { return _currentInput; }
            set
            {
                if (_currentInput == value) return;
                _currentInput = value;
                OnPropertyChanged();
                ((RelayCommand)SendCommand).RaiseCanExecuteChanged();
            }
        }

        public string SelectedPdfPath
        {
            get { return _selectedPdfPath; }
            set
            {
                if (_selectedPdfPath == value) return;
                _selectedPdfPath = value;
                OnPropertyChanged();
            }
        }

        public string SelectedWordPath
        {
            get { return _selectedWordPath; }
            set
            {
                if (_selectedWordPath == value) return;
                _selectedWordPath = value;
                OnPropertyChanged();
            }
        }

        public string WebsiteUrl
        {
            get { return _websiteUrl; }
            set
            {
                if (_websiteUrl == value) return;
                _websiteUrl = value;
                OnPropertyChanged();
            }
        }

        // =========================
        // Commands
        // =========================
        public ICommand SendCommand { get; private set; }
        public ICommand BrowsePdfCommand { get; private set; }
        public ICommand BrowseWordCommand { get; private set; }
        public ICommand LoadWebsiteCommand { get; private set; }
        public ICommand NewChatCommand { get; private set; }

        // =========================
        // Intent model
        // =========================
        private enum QueryKind
        {
            General,
            SummarizeDocument,
            SummarizePage,
            ExtractPage,
            Find,
            Help
        }

        private class QueryIntent
        {
            public QueryKind Kind { get; set; }
            public int PageNumber { get; set; }
            public string FindKeyword { get; set; }
        }

        public MainViewModel()
        {
            Messages = new ObservableCollection<ChatMessage>();

            _currentInput = "";
            _selectedPdfPath = "(no file selected)";
            _selectedWordPath = "(no file selected)";
            _websiteUrl = "";

            SendCommand = new RelayCommand(async o => await SendAsync(), o => CanSend());
            BrowsePdfCommand = new RelayCommand(o => BrowsePdf());
            BrowseWordCommand = new RelayCommand(o => BrowseWord());
            LoadWebsiteCommand = new RelayCommand(async o => await LoadWebsiteAsync());
            NewChatCommand = new RelayCommand(o => NewChat());

            Messages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Text = "Upload a PDF/Word or load a Website and ask me a question 🙂 (Type 'help' for commands)"
            });
        }

        // =========================
        // Core chat flow
        // =========================
        private bool CanSend()
        {
            return !string.IsNullOrWhiteSpace(CurrentInput);
        }

        private async Task SendAsync()
        {
            string text = (CurrentInput ?? "").Trim();
            CurrentInput = "";

            if (string.IsNullOrWhiteSpace(text))
                return;

            Messages.Add(new ChatMessage { Role = ChatRole.User, Text = text });

            var typing = new ChatMessage { Role = ChatRole.Assistant, Text = "Typing…" };
            Messages.Add(typing);

            try
            {
                await Task.Delay(90);

                // Follow-up handling
                string normalized = text.ToLowerInvariant();
                if (IsFollowUp(normalized) && !string.IsNullOrWhiteSpace(_lastUserQuestion))
                    text = _lastUserQuestion + " (follow-up: " + text + ")";
                else
                    _lastUserQuestion = text;

                var intent = DetectIntent(text);

                // Help works without any source
                if (intent.Kind == QueryKind.Help)
                {
                    Messages.Remove(typing);
                    Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = HelpText() });
                    return;
                }

                // Ensure we have some content loaded for the active tab
                string notReady = GetNotReadyMessage();
                if (!string.IsNullOrEmpty(notReady))
                {
                    Messages.Remove(typing);
                    Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = notReady });
                    return;
                }

                string answer;

                if (intent.Kind == QueryKind.Find)
                {
                    answer = await AnswerFindAsync(intent.FindKeyword);
                }
                else if (intent.Kind == QueryKind.SummarizePage)
                {
                    answer = AnswerSummarizeSpecificPage(intent.PageNumber);
                }
                else if (intent.Kind == QueryKind.SummarizeDocument)
                {
                    answer = AnswerSummarizeDocument();
                }
                else if (intent.Kind == QueryKind.ExtractPage)
                {
                    answer = AnswerShowPageSnippet(intent.PageNumber);
                }
                else
                {
                    answer = await AnswerWithRetrievalAsync(text);
                }

                Messages.Remove(typing);
                Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = answer });
            }
            catch
            {
                Messages.Remove(typing);
                Messages.Add(new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Text = "Something went wrong while processing the document. Try re-loading or ask a shorter question."
                });
            }
        }

        private string GetNotReadyMessage()
        {
            if (_pages == null || _pages.Count == 0)
            {
                if (ActiveSource == SourceKind.Pdf)
                    return (SelectedPdfPath == "(no file selected)") ? "Pick a PDF first (PDF tab) and try again." : "PDF is selected but no text is loaded. Try another PDF (or OCR if scanned).";

                if (ActiveSource == SourceKind.Word)
                    return (SelectedWordPath == "(no file selected)") ? "Pick a Word file first (Word tab) and try again." : "Word is selected but no text is loaded.";

                if (ActiveSource == SourceKind.Web)
                    return string.IsNullOrWhiteSpace(WebsiteUrl) ? "Paste a URL (Web tab) then click Load Website." : "Website URL is set but content not loaded. Click Load Website.";

                return "Load a document first.";
            }

            return null;
        }

        // =========================
        // Browse / Load commands
        // =========================
        private void BrowsePdf()
        {
            ActiveSource = SourceKind.Pdf;

            var dlg = new OpenFileDialog
            {
                Title = "Select PDF",
                Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dlg.ShowDialog() == true)
            {
                SelectedPdfPath = dlg.FileName;

                try
                {
                    _pages = PdfTextService.ReadAllPages(SelectedPdfPath);
                    BuildDocVocabulary();

                    Messages.Add(new ChatMessage
                    {
                        Role = ChatRole.Assistant,
                        Text = "PDF loaded ✅ Pages: " + _pages.Count
                    });
                }
                catch
                {
                    _pages = new List<PageText>();
                    _docVocab = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    Messages.Add(new ChatMessage
                    {
                        Role = ChatRole.Assistant,
                        Text = "PDF selected, but I couldn’t extract text. If it’s scanned, you’ll need OCR."
                    });
                }
            }
        }

        private void BrowseWord()
        {
            ActiveSource = SourceKind.Word;

            var dlg = new OpenFileDialog
            {
                Title = "Select Word (.docx)",
                Filter = "Word files (*.docx)|*.docx|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dlg.ShowDialog() == true)
            {
                SelectedWordPath = dlg.FileName;

                try
                {
                    // You must implement this service (I can provide it next)
                    _pages = DocxTextService.ReadAllPages(SelectedWordPath);
                    BuildDocVocabulary();

                    Messages.Add(new ChatMessage
                    {
                        Role = ChatRole.Assistant,
                        Text = "Word loaded ✅ Sections: " + _pages.Count
                    });
                }
                catch
                {
                    _pages = new List<PageText>();
                    _docVocab = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    Messages.Add(new ChatMessage
                    {
                        Role = ChatRole.Assistant,
                        Text = "Word selected, but I couldn’t read it. Make sure it’s .docx (not .doc)."
                    });
                }
            }
        }

        private async Task LoadWebsiteAsync()
        {
            ActiveSource = SourceKind.Web;

            if (string.IsNullOrWhiteSpace(WebsiteUrl))
            {
                Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = "Paste a URL first." });
                return;
            }

            try
            {
                // You must implement this service (I can provide it next)
                _pages = await WebTextService.LoadChunksAsync(WebsiteUrl);
                BuildDocVocabulary();

                Messages.Add(new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Text = "Website loaded ✅ Chunks: " + _pages.Count
                });
            }
            catch
            {
                _pages = new List<PageText>();
                _docVocab = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                Messages.Add(new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Text = "Couldn’t load the website. Try another URL or check internet access."
                });
            }
        }

        private void NewChat()
        {
            Messages.Clear();
            _lastUserQuestion = "";

            Messages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Text = "New chat started. Type 'help' to see commands 🙂"
            });
        }

        // =========================
        // Intent + commands
        // =========================
        private bool IsFollowUp(string lower)
        {
            return lower == "explain more"
                || lower == "tell me more"
                || lower == "more"
                || lower.Contains("explain that")
                || lower.Contains("what about that")
                || lower.Contains("what do you mean")
                || lower.Contains("elaborate");
        }

        private QueryIntent DetectIntent(string text)
        {
            var lower = (text ?? "").Trim().ToLowerInvariant();

            if (lower == "help" || lower == "/help" || lower == "?" || lower == "commands")
                return new QueryIntent { Kind = QueryKind.Help };

            if (lower.StartsWith("find:"))
            {
                string keyword = text.Substring(5).Trim();
                return new QueryIntent { Kind = QueryKind.Find, FindKeyword = keyword };
            }

            int page = TryGetPageNumber(lower);

            if (lower.Contains("summary") || lower.Contains("summarize") || lower.Contains("gist") || lower.Contains("overview"))
            {
                if (page > 0) return new QueryIntent { Kind = QueryKind.SummarizePage, PageNumber = page };
                return new QueryIntent { Kind = QueryKind.SummarizeDocument };
            }

            if (page > 0 && (lower.StartsWith("page ") || lower.Contains("show page") || lower.Contains("open page") || lower.Contains("what is on page")))
            {
                return new QueryIntent { Kind = QueryKind.ExtractPage, PageNumber = page };
            }

            return new QueryIntent { Kind = QueryKind.General };
        }

        private string HelpText()
        {
            return
@"Commands you can use:

• help
• summary
• summary page 7
• page 7
• find: payment terms
• find: refund policy

Tip:
- If you type with small typos, I try to fix them.
- I also expand synonyms (free) to improve search.";
        }

        private int TryGetPageNumber(string lower)
        {
            int idx = lower.IndexOf("page ");
            if (idx < 0) return -1;

            idx += 5;
            string num = "";

            while (idx < lower.Length)
            {
                char c = lower[idx];
                if (c >= '0' && c <= '9') num += c;
                else break;
                idx++;
            }

            int page;
            if (int.TryParse(num, out page)) return page;
            return -1;
        }

        // =========================
        // Answers (smarter search)
        // =========================

        private async Task<string> AnswerFindAsync(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword) || keyword.Trim().Length < 2)
                return "Type like this: find: payment terms";

            var enriched = await EnrichQueryAsync(keyword.Trim());

            // Try expanded first
            var hits = PdfSearchService.Search(_pages, enriched.ExpandedQuery, 8);

            // If expansion made it worse, fallback to corrected-only
            if (hits == null || hits.Count == 0)
                hits = PdfSearchService.Search(_pages, enriched.CorrectedQuery, 8);

            if (hits == null || hits.Count == 0)
                return "No matches found for: " + enriched.CorrectedQuery;

            var sb = new StringBuilder();
            sb.AppendLine("Top matches for: " + enriched.CorrectedQuery);
            sb.AppendLine();

            for (int i = 0; i < hits.Count; i++)
            {
                sb.AppendLine("Page " + hits[i].PageNumber);
                sb.AppendLine(hits[i].Snippet);
                sb.AppendLine();
            }

            sb.AppendLine("Try: summary page " + hits[0].PageNumber);
            return sb.ToString().Trim();
        }

        private async Task<string> AnswerWithRetrievalAsync(string question)
        {
            var enriched = await EnrichQueryAsync(question);

            var hits = PdfSearchService.Search(_pages, enriched.ExpandedQuery, 12);
            if (hits == null || hits.Count == 0)
                hits = PdfSearchService.Search(_pages, enriched.CorrectedQuery, 12);

            if (hits == null || hits.Count == 0)
                return "I couldn’t find anything relevant.\nTry \"find: <keyword>\" or \"summary page X\".";

            var top = new List<int>();
            for (int i = 0; i < hits.Count; i++)
                if (!top.Contains(hits[i].PageNumber)) top.Add(hits[i].PageNumber);

            top.Sort();

            var sb = new StringBuilder();

            if (!string.Equals(enriched.CorrectedQuery, question, StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("I searched for: " + enriched.CorrectedQuery);
                sb.AppendLine();
            }

            sb.AppendLine("Answer (based on closest matches):");
            sb.AppendLine();

            int used = 0;
            for (int i = 0; i < top.Count && used < 2; i++)
            {
                var p = GetPage(top[i]);
                if (p == null) continue;

                var key = ExtractKeySentences(p.Text, 3);
                if (key.Count == 0) continue;

                for (int k = 0; k < key.Count; k++)
                    sb.AppendLine("• " + key[k]);

                used++;
                sb.AppendLine();
            }

            sb.Append("Evidence pages: ");
            for (int i = 0; i < top.Count && i < 5; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(top[i]);
            }

            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Try: find: <keyword>  |  summary  |  summary page 5  |  page 5");

            return sb.ToString().Trim();
        }

        private string AnswerSummarizeDocument()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Document overview (quick summary):");
            sb.AppendLine();

            int takePages = _pages.Count < 3 ? _pages.Count : 3;

            for (int i = 0; i < takePages; i++)
            {
                var p = _pages[i];
                var bullets = ExtractBulletLikeLines(p.Text, 4);
                if (bullets.Count > 0)
                {
                    sb.AppendLine("Page " + p.PageNumber + ":");
                    for (int b = 0; b < bullets.Count; b++)
                        sb.AppendLine("• " + bullets[b]);
                    sb.AppendLine();
                }
            }

            if (sb.ToString().Trim().EndsWith("quick summary):"))
            {
                sb.AppendLine("I couldn’t detect clean headings/bullets. Ask: \"summary page 1\" or use \"find: <keyword>\".");
            }

            sb.AppendLine("Tell me your angle (scope, risks, timeline, cost) and I’ll summarize that.");
            return sb.ToString().Trim();
        }

        private string AnswerSummarizeSpecificPage(int pageNumber)
        {
            var p = GetPage(pageNumber);
            if (p == null) return "I can’t find page " + pageNumber + ". This document has " + _pages.Count + " pages/chunks.";

            var bullets = ExtractBulletLikeLines(p.Text, 7);
            if (bullets.Count == 0)
                bullets = ExtractKeySentences(p.Text, 5);

            var sb = new StringBuilder();
            sb.AppendLine("Summary of page " + pageNumber + ":");
            sb.AppendLine();

            for (int i = 0; i < bullets.Count; i++)
                sb.AppendLine("• " + bullets[i]);

            return sb.ToString().Trim();
        }

        private string AnswerShowPageSnippet(int pageNumber)
        {
            var p = GetPage(pageNumber);
            if (p == null) return "I can’t find page " + pageNumber + ". This document has " + _pages.Count + " pages/chunks.";

            string snippet = MakeSnippet(p.Text, 900);
            if (string.IsNullOrWhiteSpace(snippet)) snippet = "(No extractable text found on this page.)";

            return "Page " + pageNumber + " (excerpt):\n\n" + snippet;
        }

        private PageText GetPage(int pageNumber)
        {
            for (int i = 0; i < _pages.Count; i++)
                if (_pages[i].PageNumber == pageNumber) return _pages[i];
            return null;
        }

        // =========================
        // Query enrichment (safer)
        // =========================
        private class EnrichedQuery
        {
            public string CorrectedQuery;
            public string ExpandedQuery;
        }

        private async Task<EnrichedQuery> EnrichQueryAsync(string query)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0)
                return new EnrichedQuery { CorrectedQuery = "", ExpandedQuery = "" };

            var terms = Tokenize(query);

            var correctedTerms = new List<string>();
            for (int i = 0; i < terms.Count; i++)
                correctedTerms.Add(CorrectUsingDocVocab(terms[i]));

            string correctedQuery = string.Join(" ", correctedTerms.ToArray());

            // Expand only a little, and only for "content-ish" words
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < correctedTerms.Count; i++)
            {
                string t = correctedTerms[i];
                if (IsStopWord(t)) { expanded.Add(t); continue; }

                expanded.Add(t);

                var syns = await GetSynonymsFreeAsync(t, 3);
                for (int s = 0; s < syns.Count; s++)
                    expanded.Add(syns[s]);
            }

            string expandedQuery = string.Join(" ", expanded.ToArray());

            return new EnrichedQuery
            {
                CorrectedQuery = correctedQuery,
                ExpandedQuery = expandedQuery
            };
        }

        private bool IsStopWord(string w)
        {
            if (string.IsNullOrWhiteSpace(w)) return true;
            if (w.Length <= 2) return true;
            if (char.IsDigit(w[0])) return true;

            // small list is enough
            string[] stop = new string[] {
                "the","a","an","and","or","but","to","of","in","on","for","with","by","as","at","is","are","was","were",
                "be","been","being","this","that","these","those","it","its","from","into","about"
            };

            for (int i = 0; i < stop.Length; i++)
                if (string.Equals(stop[i], w, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        private void BuildDocVocabulary()
        {
            _docVocab = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < _pages.Count; i++)
            {
                var text = _pages[i].Text ?? "";
                var words = Tokenize(text);

                for (int w = 0; w < words.Count; w++)
                {
                    if (words[w].Length > 1)
                        _docVocab.Add(words[w]);
                }
            }
        }

        private string CorrectUsingDocVocab(string term)
        {
            if (string.IsNullOrWhiteSpace(term)) return term;
            if (_docVocab == null || _docVocab.Count == 0) return term;
            if (_docVocab.Contains(term)) return term;

            // Don’t “correct” numbers / short words
            if (term.Length <= 3) return term;
            if (char.IsDigit(term[0])) return term;

            string best = term;
            int bestDist = int.MaxValue;

            char first = char.ToLowerInvariant(term[0]);

            foreach (var v in _docVocab)
            {
                if (v.Length < 2) continue;
                if (char.ToLowerInvariant(v[0]) != first) continue;

                int d = Levenshtein(term.ToLowerInvariant(), v.ToLowerInvariant(), 2);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = v;
                    if (bestDist == 0) break;
                }
            }

            return (bestDist <= 2) ? best : term;
        }

        private int Levenshtein(string a, string b, int maxDist)
        {
            int n = a.Length, m = b.Length;
            if (Math.Abs(n - m) > maxDist) return maxDist + 1;

            int[] prev = new int[m + 1];
            int[] curr = new int[m + 1];

            for (int j = 0; j <= m; j++) prev[j] = j;

            for (int i = 1; i <= n; i++)
            {
                curr[0] = i;
                int minInRow = curr[0];

                for (int j = 1; j <= m; j++)
                {
                    int cost = (a[i - 1] == b[j - 1]) ? 0 : 1;
                    int ins = curr[j - 1] + 1;
                    int del = prev[j] + 1;
                    int sub = prev[j - 1] + cost;

                    int val = ins < del ? ins : del;
                    if (sub < val) val = sub;

                    curr[j] = val;
                    if (val < minInRow) minInRow = val;
                }

                if (minInRow > maxDist) return maxDist + 1;

                var tmp = prev;
                prev = curr;
                curr = tmp;
            }

            return prev[m];
        }

        private List<string> Tokenize(string text)
        {
            var terms = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return terms;

            var sb = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
                else
                {
                    if (sb.Length > 0)
                    {
                        terms.Add(sb.ToString());
                        sb.Length = 0;
                    }
                }
            }
            if (sb.Length > 0) terms.Add(sb.ToString());

            return terms.Where(t => t.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private async Task<List<string>> GetSynonymsFreeAsync(string word, int max)
        {
            if (string.IsNullOrWhiteSpace(word)) return new List<string>();
            if (word.Length < 2) return new List<string>();

            string key = word.ToLowerInvariant() + "|" + max;
            if (_synCache.ContainsKey(key))
                return _synCache[key];

            var result = new List<string>();

            try
            {
                string url = "https://api.datamuse.com/words?rel_syn=" + Uri.EscapeDataString(word) + "&max=" + max;
                using (var stream = await _http.GetStreamAsync(url))
                {
                    var ser = new DataContractJsonSerializer(typeof(DatamuseWord[]));
                    var arr = (DatamuseWord[])ser.ReadObject(stream);

                    if (arr != null)
                    {
                        for (int i = 0; i < arr.Length; i++)
                        {
                            var w = arr[i] != null ? arr[i].word : null;
                            if (!string.IsNullOrWhiteSpace(w))
                                result.Add(w.Trim().ToLowerInvariant());
                        }
                    }
                }
            }
            catch
            {
                // ignore network errors
            }

            result = result.Where(x => !string.Equals(x, word, StringComparison.OrdinalIgnoreCase))
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .ToList();

            _synCache[key] = result;
            return result;
        }

        [DataContract]
        private class DatamuseWord
        {
            [DataMember] public string word { get; set; }
            [DataMember] public int score { get; set; }
        }

        // =========================
        // Text helpers
        // =========================
        private List<string> ExtractBulletLikeLines(string text, int max)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length < 6) continue;

                if (line.StartsWith("•") || line.StartsWith("-") || line.StartsWith("*") || LooksLikeHeading(line))
                {
                    result.Add(CleanLine(line));
                    if (result.Count >= max) break;
                }
            }
            return result;
        }

        private bool LooksLikeHeading(string line)
        {
            if (line.Length > 70) return false;

            int dots = 0;
            for (int i = 0; i < line.Length; i++)
                if (line[i] == '.') dots++;

            return dots == 0 && line.Length >= 10;
        }

        private string CleanLine(string s)
        {
            return s.Replace("\t", " ").Replace("  ", " ").Trim();
        }

        private List<string> ExtractKeySentences(string text, int max)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            var parts = text.Replace("\r", " ").Replace("\n", " ").Split(new[] { '.', '?', '!' });
            for (int i = 0; i < parts.Length; i++)
            {
                var sentence = parts[i].Trim();
                if (sentence.Length < 25) continue;
                if (sentence.Length > 220) sentence = sentence.Substring(0, 220).Trim();

                result.Add(sentence + ".");
                if (result.Count >= max) break;
            }
            return result;
        }

        private string MakeSnippet(string text, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length <= maxChars) return text;
            return text.Substring(0, maxChars).Trim() + " ...";
        }
    }
}
