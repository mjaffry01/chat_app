namespace pdf_chat_app.Models
{
    public class EmbeddingChunk
    {
        public int PageNumber { get; set; }
        public string Text { get; set; } = "";
        public float[] Vector { get; set; } = new float[0];
    }
}
