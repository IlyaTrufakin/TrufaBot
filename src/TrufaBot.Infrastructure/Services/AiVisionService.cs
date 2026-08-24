using System.Net.Http.Json;
using System.Text.Json;
using TrufaBot.Application.Interfaces;

namespace TrufaBot.Infrastructure.Services;

public class AiVisionService : IAiVisionService
{
    private readonly HttpClient _httpClient;

    public AiVisionService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    public async Task<(bool Success, string ModelName, string Message)> TestConnectionAsync(string serverUrl, CancellationToken ct = default)
    {
        var baseUrl = NormalizeUrl(serverUrl);
        try
        {
            var response = await _httpClient.GetAsync($"{baseUrl}/v1/models", ct);
            if (!response.IsSuccessStatusCode)
            {
                return (false, "", $"Сервер ответил с кодом {response.StatusCode}");
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(content);
            
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var dataArr) && dataArr.GetArrayLength() > 0)
            {
                var firstModel = dataArr[0].GetProperty("id").GetString() ?? "local-model";
                return (true, firstModel, $"Подключено успешно! Модель: {firstModel}");
            }

            return (true, "local-model", "Подключено к LM Studio (модель готова к работе)");
        }
        catch (Exception ex)
        {
            return (false, "", $"Не удалось подключиться к {baseUrl}: {ex.Message}");
        }
    }

    public async Task<(string Description, string Tags)> AnalyzePhotoAsync(string imagePath, string serverUrl, string modelName, CancellationToken ct = default)
    {
        var baseUrl = NormalizeUrl(serverUrl);
        if (!File.Exists(imagePath)) return ("", "");

        try
        {
            var bytes = await File.ReadAllBytesAsync(imagePath, ct);
            var base64 = Convert.ToBase64String(bytes);
            var dataUri = $"data:image/jpeg;base64,{base64}";

            var requestBody = new
            {
                model = string.IsNullOrWhiteSpace(modelName) ? "local-model" : modelName,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "Ты - эксперт-архивариус семейных фотографий. Проанализируй изображение и опиши его на русском языке. Ответ дай строго по схеме:\nОПИСАНИЕ: <1-2 емких предложения с описанием кто/что на фото, действие, место, атмосфера>\nТЕГИ: <5-10 ключевых слов через запятую>"
                    },
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = "Опиши эту фотографию и выдели ключевые теги:" },
                            new { type = "image_url", image_url = new { url = dataUri } }
                        }
                    }
                },
                temperature = 0.2,
                max_tokens = 350
            };

            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/v1/chat/completions", requestBody, ct);
            if (!response.IsSuccessStatusCode)
            {
                return ("", "");
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(content);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) return ("", "");

            var replyText = choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            return ParseAiReply(replyText);
        }
        catch
        {
            return ("", "");
        }
    }

    private static (string Description, string Tags) ParseAiReply(string text)
    {
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        string description = "";
        string tags = "";

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("ОПИСАНИЕ:", StringComparison.OrdinalIgnoreCase))
            {
                description = trimmed.Substring("ОПИСАНИЕ:".Length).Trim();
            }
            else if (trimmed.StartsWith("ТЕГИ:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("ТАГИ:", StringComparison.OrdinalIgnoreCase))
            {
                tags = trimmed.Substring(trimmed.IndexOf(':') + 1).Trim();
            }
        }

        if (string.IsNullOrEmpty(description) && !string.IsNullOrEmpty(text))
        {
            description = text.Trim();
        }

        return (description, tags);
    }

    private static string NormalizeUrl(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }
        return trimmed;
    }
}
