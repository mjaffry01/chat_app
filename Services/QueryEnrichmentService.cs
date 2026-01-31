using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.IO;

namespace pdf_chat_app.Services
{
    public static class QueryEnrichmentService
    {
        // Reuse ONE HttpClient for the whole app
        private static readonly HttpClient _http = new HttpClient();

        // Simple in-memory cache to avoid calling APIs repeatedly
        private static readonly Dictionary<string, string> _spellCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<string>> _synCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Enrich query: spell-correct + expand with synonyms.
        /// Returns (correctedQuery, expandedTerms)
        /// </summary>
        public static async Task<Tuple<string, List<string>>> EnrichAsync(string query)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0)
                return Tuple.Create("", new List<string>());

            string corrected = await SpellCorrectAsync(query);
            var terms = Tokenize(corrected);

            // Expand synonyms for each term (limit to keep search stable)
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < terms.Count; i++)
            {
                expanded.Add(terms[i]);

                var syns = await GetSynonymsAsync(terms[i], 5);
                for (int s = 0; s < syns.Count; s++)
                    expanded.Add(syns[s]);
            }

            return Tuple.Create(corrected, expanded.ToList());
        }

        // -------------------------
        // Spell correction (LanguageTool)
        // -------------------------
        private static async Task<string> SpellCorrectAsync(string text)
        {
            if (_spellCache.ContainsKey(text))
                return _spellCache[text];

            // Public endpoint: https://api.languagetool.org/v2/check :contentReference[oaicite:4]{index=4}
            var url = "https://api.languagetool.org/v2/check";

            // Use form-encoded POST
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("text", text),
                new KeyValuePair<string,string>("language", "en-US")
            });

            string corrected = text;

            // Note: if network is down, just return original text
            try
            {
                var resp = await _http.PostAsync(url, content);
                if (!resp.IsSuccessStatusCode)
                {
                    _spellCache[text] = text;
                    return text;
                }

                using (var stream = await resp.Content.ReadAsStreamAsync())
                {
                    var serializer = new DataContractJsonSerializer(typeof(LanguageToolResponse));
                    var data = (LanguageToolResponse)serializer.ReadObject(stream);

                    // Apply replacements from end to start so indexes stay valid
                    // We prefer the first replacement for each match.
                    if (data != null && data.matches != null && data.matches.Length > 0)
                    {
                        var sb = new StringBuilder(text);

                        // Sort by offset descending
                        var matches = data.matches
                            .Where(m => m != null && m.replacements != null && m.replacements.Length > 0)
                            .OrderByDescending(m => m.offset)
                            .ToList();

                        for (int i = 0; i < matches.Count; i++)
                        {
                            var m = matches[i];
                            string rep = m.replacements[0].value;
                            if (string.IsNullOrWhiteSpace(rep)) continue;

                            // Replace substring at offset/length
                            if (m.offset >= 0 && m.offset + m.length <= sb.Length)
                            {
                                sb.Remove(m.offset, m.length);
                                sb.Insert(m.offset, rep);
                            }
                        }

                        corrected = sb.ToString();
                    }
                }
            }
            catch
            {
                corrected = text;
            }

            _spellCache[text] = corrected;
            return corrected;
        }

        [DataContract]
        private class LanguageToolResponse
        {
            [DataMember] public Match[] matches { get; set; }
        }

        [DataContract]
        private class Match
        {
            [DataMember] public int offset { get; set; }
            [DataMember] public int length { get; set; }
            [DataMember] public Replacement[] replacements { get; set; }
        }

        [DataContract]
        private class Replacement
        {
            [DataMember] public string value { get; set; }
        }

        // -------------------------
        // Synonyms (Datamuse)
        // -------------------------
        private static async Task<List<string>> GetSynonymsAsync(string word, int max)
        {
            word = (word ?? "").Trim();
            if (word.Length < 2) return new List<string>();

            string cacheKey = word + "|" + max;
            if (_synCache.ContainsKey(cacheKey))
                return _synCache[cacheKey];

            // Datamuse /words endpoint supports rel_syn for synonyms :contentReference[oaicite:5]{index=5}
            string url = "https://api.datamuse.com/words?rel_syn=" + Uri.EscapeDataString(word) + "&max=" + max;


            var result = new List<string>();

            try
            {
                var json = await _http.GetStringAsync(url);
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(DatamuseWord[]));
                    var arr = (DatamuseWord[])serializer.ReadObject(ms);

                    if (arr != null)
                    {
                        for (int i = 0; i < arr.Length; i++)
                        {
                            var w = arr[i] != null ? arr[i].word : null;
                            if (!string.IsNullOrWhiteSpace(w))
                                result.Add(w.Trim());
                        }
                    }
                }
            }
            catch
            {
                // network down -> no synonyms
            }

            _synCache[cacheKey] = result;
            return result;
        }

        [DataContract]
        private class DatamuseWord
        {
            [DataMember] public string word { get; set; }
            [DataMember] public int score { get; set; }
        }

        // -------------------------
        // Tokenizer
        // -------------------------
        private static List<string> Tokenize(string text)
        {
            var terms = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return terms;

            var sb = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
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

            return terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
