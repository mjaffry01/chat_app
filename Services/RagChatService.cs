using pdf_chat_app.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace pdf_chat_app.Services
{
    public sealed class RagChatService
    {
        private readonly LlmClient _llm;
        private readonly string _embeddingModel;
        private readonly string _chatModel;

        private readonly List<EmbeddingChunk> _index = new List<EmbeddingChunk>();

        public RagChatService(LlmClient llm, string embeddingModel, string chatModel)
        {
            _llm = llm;
            _embeddingModel = embeddingModel;
            _chatModel = chatModel;
        }

        public bool HasIndex
        {
            get { return _index.Count > 0; }
        }

        public async Task IndexWebPageAsync(string url, int maxCharsPerChunk = 2500)
        {
            _index.Clear();

            var pages = await WebTextService.LoadChunksAsync(url, maxCharsPerChunk);
            foreach (var p in pages)
            {
                var vec = await _llm.CreateEmbeddingAsync(_embeddingModel, p.Text);

                _index.Add(new EmbeddingChunk
                {
                    PageNumber = p.PageNumber,
                    Text = p.Text,
                    Vector = vec
                });
            }
        }

        public async Task<string> AskAsync(string userQuestion, List<ChatTurn> history, int topK = 4)
        {
            if (!HasIndex)
                return "No document/web content is indexed yet. Load a URL first.";

            var qVec = await _llm.CreateEmbeddingAsync(_embeddingModel, userQuestion);

            var top = _index
                .Select(c => new { Chunk = c, Score = VectorMath.Cosine(qVec, c.Vector) })
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .Select(x => x.Chunk)
                .ToList();

            var context = BuildContext(top);

            var messages = new List<(string role, string content)>
            {
                ("system", "You are a helpful assistant. Answer using ONLY the provided CONTEXT. If not found, say you don't know."),
                ("system", context)
            };

            // C# 7.3-safe history handling
            if (history != null)
            {
                int start = Math.Max(0, history.Count - 8);
                for (int i = start; i < history.Count; i++)
                {
                    var t = history[i];
                    messages.Add((t.Role, t.Content));
                }
            }

            messages.Add(("user", userQuestion));

            return await _llm.ChatAsync(_chatModel, messages);
        }

        private static string BuildContext(List<EmbeddingChunk> chunks)
        {
            var sb = new StringBuilder();
            sb.AppendLine("CONTEXT:");
            sb.AppendLine("--------");

            foreach (var c in chunks)
            {
                sb.AppendLine("[Chunk p" + c.PageNumber + "]");
                sb.AppendLine(c.Text);
                sb.AppendLine();
            }

            return sb.ToString();
        }

        public async Task IndexPagesAsync(List<PageText> pages)
        {
            _index.Clear();
            if (pages == null) return;

            foreach (var p in pages)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Text)) continue;

                var vec = await _llm.CreateEmbeddingAsync(_embeddingModel, p.Text);

                _index.Add(new EmbeddingChunk
                {
                    PageNumber = p.PageNumber,
                    Text = p.Text,
                    Vector = vec
                });
            }
        }

    }
}
