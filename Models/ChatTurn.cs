namespace pdf_chat_app.Models
{
    public class ChatTurn
    {
        public string Role { get; set; } = "user"; // "user" | "assistant" | "system"
        public string Content { get; set; } = "";
    }
}
