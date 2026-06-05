using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OwTranslateLite.Core;

namespace OwTranslateLite.Translation;

public sealed class OpenAICompatibleTranslationProvider : ITranslationProvider
{
    private readonly AppSettings _settings;
    private readonly OwGlossaryService _glossary;
    private readonly HttpClient _client;

    public OpenAICompatibleTranslationProvider(AppSettings settings, OwGlossaryService glossary)
    {
        _settings = settings;
        _glossary = glossary;
        _client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 5, 90))
        };
    }

    public string Name => _settings.TranslationProvider;

    public async Task<IReadOnlyList<TranslationResult>> TranslateAsync(IReadOnlyList<ParsedChatLine> lines, CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
        {
            return Array.Empty<TranslationResult>();
        }

        if (string.IsNullOrWhiteSpace(_settings.ApiUrl) || string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            return lines.Select(line => new TranslationResult(line, "需要配置 API Key")).ToList();
        }

        using HttpRequestMessage request = new(HttpMethod.Post, BuildChatCompletionsUri(_settings.ApiUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);

        object payload = new
        {
            model = string.IsNullOrWhiteSpace(_settings.Model) ? "deepseek-v4-flash" : _settings.Model,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "你是守望先锋2实时竞技聊天翻译器。把玩家发言翻译为简体中文。只输出有效JSON，不要Markdown，不要解释。不要翻译玩家ID。英雄、技能、地图、战术俚语使用中国玩家常用叫法。普通韩语、日语、英语、俄语等自然语言必须完整翻译；OW术语命中只作为约束，不要把整句退化成术语替换。译文要短、自然、适合游戏内快速阅读。"
                },
                new
                {
                    role = "user",
                    content = JsonSerializer.Serialize(new
                    {
                        style = "简体中文，短句，竞技语境，不解释",
                        output_schema = new
                        {
                            translations = new[]
                            {
                                new { id = "原样返回id", text = "中文译文" }
                            }
                        },
                        messages = lines.Select((line, index) => new
                        {
                            id = index.ToString(),
                            speaker = line.Speaker,
                            text = line.SourceText,
                            glossary_hits = _glossary.BuildPromptContext(line.GlossaryHits)
                        }).ToArray()
                    })
                }
            }
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
        string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        Dictionary<string, string> translations = ExtractTranslations(responseText);
        return lines.Select((line, index) =>
        {
            string key = index.ToString();
            string translated = translations.TryGetValue(key, out string? value) ? value : line.SourceText;
            translated = CleanupModelText(translated);
            translated = _glossary.ApplyTerms(translated);
            return new TranslationResult(line, translated);
        }).ToList();
    }

    public static async Task<IReadOnlyList<string>> FetchModelIdsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiUrl) || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return Array.Empty<string>();
        }

        using HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 5, 90))
        };
        using HttpRequestMessage request = new(HttpMethod.Get, BuildModelsUri(settings.ApiUrl));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(responseText);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out JsonElement id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id)
            .ToList();
    }

    private static Uri BuildChatCompletionsUri(string apiUrl)
    {
        Uri uri = new(apiUrl.Trim().TrimEnd('/'));
        if (uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        return new Uri($"{uri.AbsoluteUri.TrimEnd('/')}/chat/completions");
    }

    private static Uri BuildModelsUri(string apiUrl)
    {
        Uri uri = new(apiUrl.Trim().TrimEnd('/'));
        string absolute = uri.AbsoluteUri.TrimEnd('/');
        if (absolute.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            absolute = absolute[..^"/chat/completions".Length];
        }

        return new Uri($"{absolute.TrimEnd('/')}/models");
    }

    private static Dictionary<string, string> ExtractTranslations(string responseText)
    {
        using JsonDocument document = JsonDocument.Parse(responseText);
        string content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        content = content.Trim().Trim('`').Replace("```json", "", StringComparison.OrdinalIgnoreCase).Replace("```", "").Trim();

        Dictionary<string, string> result = new(StringComparer.Ordinal);
        using JsonDocument inner = JsonDocument.Parse(content);
        if (!inner.RootElement.TryGetProperty("translations", out JsonElement translations) ||
            translations.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (JsonElement item in translations.EnumerateArray())
        {
            string? id = item.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString() : null;
            string? text = item.TryGetProperty("text", out JsonElement textElement) ? textElement.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(text))
            {
                result[id] = text;
            }
        }

        return result;
    }

    private static string CleanupModelText(string text)
    {
        string result = text.Trim();
        result = result.Trim('`');
        result = result.Replace("```json", "", StringComparison.OrdinalIgnoreCase)
            .Replace("```", "")
            .Trim();
        return result.Length > 120 ? result[..120] : result;
    }
}
