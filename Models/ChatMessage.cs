using System;

namespace pdf_chat_app.Models
{
    public enum ChatRole
    {
        User,
        Assistant
    }

    public class ChatMessage
    {
        public ChatRole Role { get; set; }
        public string Text { get; set; }
        public DateTime Timestamp { get; set; }

        public ChatMessage()
        {
            Text = "";
            Timestamp = DateTime.Now;
        }
    }
}
