using System.Text;
using System.Text.Json;

namespace MultiPointAutoClicker;

public static class TelegramService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<string?> GetLatestChatIdAsync(string token)
    {
        using var response = await Client.GetAsync("https://api.telegram.org/bot" + token + "/getUpdates");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(ReadError(root));
        var updates = root.GetProperty("result");
        for (var i = updates.GetArrayLength() - 1; i >= 0; i--)
        {
            var update = updates[i];
            if (update.TryGetProperty("message", out var message) && message.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var id)) return id.ToString();
        }
        return null;
    }

    public static async Task SendMessageAsync(string token, string chatId, string message)
    {
        var payload = JsonSerializer.Serialize(new { chat_id = chatId, text = message });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync("https://api.telegram.org/bot" + token + "/sendMessage", content);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!response.IsSuccessStatusCode || !doc.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(ReadError(doc.RootElement));
    }

    private static string ReadError(JsonElement root) => root.TryGetProperty("description", out var d) ? d.GetString() ?? "Ошибка Telegram." : "Не удалось связаться с Telegram.";
}