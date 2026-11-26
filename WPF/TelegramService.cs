using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MultiPointAutoClicker;

public sealed record TelegramIncomingUpdate(long UpdateId, string? ChatId, string? FromId, string? Text);

public static class TelegramService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

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

    public static async Task<List<TelegramIncomingUpdate>> GetUpdatesAsync(string token, long offset, CancellationToken cancellationToken)
    {
        var url = "https://api.telegram.org/bot" + token + "/getUpdates?timeout=20&allowed_updates=%5B%22message%22%5D";
        if (offset > 0) url += "&offset=" + offset;
        using var response = await Client.GetAsync(url, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!response.IsSuccessStatusCode || !root.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException(ReadError(root));
        var result = new List<TelegramIncomingUpdate>();
        foreach (var update in root.GetProperty("result").EnumerateArray())
        {
            var updateId = update.GetProperty("update_id").GetInt64();
            if (!update.TryGetProperty("message", out var message)) { result.Add(new(updateId, null, null, null)); continue; }
            string? chatId = null, fromId = null, text = null;
            if (message.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var chatValue)) chatId = chatValue.ToString();
            if (message.TryGetProperty("from", out var from) && from.TryGetProperty("id", out var fromValue)) fromId = fromValue.ToString();
            if (message.TryGetProperty("text", out var textValue)) text = textValue.GetString();
            result.Add(new(updateId, chatId, fromId, text));
        }
        return result;
    }

    public static async Task SendControlPanelAsync(string token, string chatId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            chat_id = chatId,
            text = "Пульт управления MultiPoint AutoClicker:",
            reply_markup = new
            {
                keyboard = new[]
                {
                    new[] { new { text = "▶ Запустить" }, new { text = "■ Остановить" } },
                    new[] { new { text = "ℹ Статус" } }
                },
                resize_keyboard = true,
                is_persistent = true
            }
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync("https://api.telegram.org/bot" + token + "/sendMessage", content);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!response.IsSuccessStatusCode || !doc.RootElement.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException(ReadError(doc.RootElement));
    }

    public static async Task SendMessageAsync(string token, string chatId, string message)
    {
        var payload = JsonSerializer.Serialize(new { chat_id = chatId, text = message });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync("https://api.telegram.org/bot" + token + "/sendMessage", content);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!response.IsSuccessStatusCode || !doc.RootElement.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException(ReadError(doc.RootElement));
    }

    public static async Task SendPhotoAsync(string token, string chatId, string photoPath, string caption)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(chatId), "chat_id");
        form.Add(new StringContent(caption), "caption");
        await using var stream = File.OpenRead(photoPath);
        using var photo = new StreamContent(stream);
        photo.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(photoPath));
        form.Add(photo, "photo", Path.GetFileName(photoPath));
        using var response = await Client.PostAsync("https://api.telegram.org/bot" + token + "/sendPhoto", form);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!response.IsSuccessStatusCode || !doc.RootElement.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException(ReadError(doc.RootElement));
    }

    private static string GetMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", _ => "image/jpeg"
    };

    private static string ReadError(JsonElement root) =>
        root.TryGetProperty("description", out var d) ? d.GetString() ?? "Ошибка Telegram." : "Не удалось связаться с Telegram.";
}
