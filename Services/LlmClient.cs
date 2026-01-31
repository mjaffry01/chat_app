using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace pdf_chat_app.Services
{
    public sealed class LlmClient
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;

        // For OpenAI:
        // baseUrl = "https://api.openai.com/v1/"
        // For Azure OpenAI: baseUrl = "https://{resource}.openai.azure.com/"
        private readonly string _baseUrl;

        public LlmClient(HttpClient http, string baseUrl, string apiKey)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _baseUrl = baseUrl?.TrimEnd('/') + "/" ?? throw new ArgumentNullException(nameof(baseUrl));
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        }

        public async Task<float[]> CreateEmbeddingAsync(string model, string input)
        {
            var url = _baseUrl + "embeddings";
            var req = new HttpRequestMessage(HttpMethod.Post, url);

            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            var payload = new
            {
                model,
                input
            };

            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

             var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            // data[0].embedding = float[]
            var emb = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");

            var vec = new float[emb.GetArrayLength()];
            int i = 0;
            foreach (var n in emb.EnumerateArray())
                vec[i++] = n.GetSingle();

            return vec;
        }

        public async Task<string> ChatAsync(string model, List<(string role, string content)> messages, double temperature = 0.2)
        {
            var url = _baseUrl + "chat/completions";
            var req = new HttpRequestMessage(HttpMethod.Post, url);

            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            var payload = new
            {
                model,
                temperature,
                messages = messages.ConvertAll(m => new { role = m.role, content = m.content })
            };

            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            return doc.RootElement
                      .GetProperty("choices")[0]
                      .GetProperty("message")
                      .GetProperty("content")
                      .GetString() ?? "";
        }
    }
}
