using System.Text;
using TMPro;

namespace TVSync
{
    /// <summary>
    /// Helper for displaying messages in the game's chat HUD.
    /// </summary>
    public static class ChatHelper
    {
        private static string _lastMessage = "";

        public static void ShowMessage(HUDManager hud, string message, string senderName)
        {
            if (hud == null) return;
            if (string.IsNullOrEmpty(message)) return;
            // Allow duplicate messages for sync feedback
            // But deduplicate rapid-fire identical messages
            if (_lastMessage == message) return;
            _lastMessage = message;

            hud.PingHUDElement(hud.Chat, 4f, 1f, 0.2f);

            if (hud.ChatMessageHistory.Count >= 4)
            {
                hud.ChatMessageHistory.RemoveAt(0);
            }

            string formatted;
            if (!string.IsNullOrEmpty(senderName))
            {
                formatted = $"<color=#00BFFF>{senderName}</color>: <color=#FFFFFF>{message}</color>";
            }
            else
            {
                formatted = $"<color=#7069ff>{message}</color>";
            }

            hud.ChatMessageHistory.Add(formatted);

            // Rebuild chat text
            var sb = new StringBuilder();
            for (int i = 0; i < hud.ChatMessageHistory.Count; i++)
            {
                sb.Append("\n");
                sb.Append(hud.ChatMessageHistory[i]);
            }
            ((TMP_Text)hud.chatText).text = sb.ToString();
        }

        public static void ResetDedup()
        {
            _lastMessage = "";
        }
    }
}
