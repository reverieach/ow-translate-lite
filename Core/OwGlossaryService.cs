using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OwTranslateLite.Core;

public sealed class OwGlossaryService
{
    private readonly List<GlossaryEntry> _entries = [];
    private readonly List<string> _ignorePhrases = [];

    public string Version { get; private set; } = "unknown";
    public int EntryCount => _entries.Count;

    public static OwGlossaryService LoadDefault()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Resources", "OwGlossary.zh-CN.json");
        if (!File.Exists(path))
        {
            path = Path.Combine(Environment.CurrentDirectory, "Resources", "OwGlossary.zh-CN.json");
        }

        string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
        GlossaryFile file = JsonSerializer.Deserialize<GlossaryFile>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new GlossaryFile();

        OwGlossaryService service = new();
        service.Version = file.Version ?? "unknown";
        service._ignorePhrases.AddRange(file.IgnorePhrases ?? []);
        service._entries.AddRange(file.Entries ?? []);

        return service;
    }

    public string NormalizeOcrText(string text)
    {
        string result = text.Trim();
        result = Regex.Replace(result, @"\s+", " ");
        result = result.Replace("：", ":").Replace("﹕", ":").Replace("｜", "|");
        result = result.Replace("D Va", "D.Va", StringComparison.OrdinalIgnoreCase);
        result = Regex.Replace(result, @"\b([oO])\s*T\b", "OT");
        result = Regex.Replace(result, @"\bI\s+need\b", "I need", RegexOptions.IgnoreCase);
        return result;
    }

    public bool ShouldIgnoreLine(string text)
    {
        string normalized = NormalizeOcrText(text);
        if (normalized.Length < 2)
        {
            return true;
        }

        if (_ignorePhrases.Any(phrase => normalized.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (Regex.IsMatch(normalized, @"^[\p{P}\p{S}\d\s]+$"))
        {
            return true;
        }

        return false;
    }

    public IReadOnlyList<GlossaryHit> FindHits(string text)
    {
        List<GlossaryHit> hits = [];
        string normalizedText = NormalizeKey(text);

        foreach (GlossaryEntry entry in _entries)
        {
            foreach (string term in entry.Terms ?? [])
            {
                string key = NormalizeKey(term);
                if (key.Length == 0)
                {
                    continue;
                }

                bool hit = key.Length <= 3
                    ? Regex.IsMatch(normalizedText, $@"(^| ){Regex.Escape(key)}( |$)")
                    : normalizedText.Contains(key, StringComparison.OrdinalIgnoreCase);

                if (hit)
                {
                    hits.Add(new GlossaryHit(term, entry.ZhCn ?? term, entry.Category ?? "term"));
                    break;
                }
            }
        }

        return hits
            .GroupBy(hit => hit.Target)
            .Select(group => group.First())
            .Take(12)
            .ToList();
    }

    public string ApplyTerms(string text)
    {
        string result = text;
        foreach (GlossaryHit hit in FindHits(text))
        {
            result = Regex.Replace(result, Regex.Escape(hit.Source), hit.Target, RegexOptions.IgnoreCase);
        }

        return result;
    }

    public string BuildPromptContext(IReadOnlyList<GlossaryHit> hits)
    {
        if (hits.Count == 0)
        {
            return "无术语命中。";
        }

        return string.Join("; ", hits.Select(hit => $"{hit.Source}->{hit.Target}({hit.Category})"));
    }

    private static string NormalizeKey(string value)
    {
        string lower = value.ToLowerInvariant();
        lower = Regex.Replace(lower, @"[^\p{L}\p{N}]+", " ");
        return Regex.Replace(lower, @"\s+", " ").Trim();
    }

    private sealed class GlossaryFile
    {
        public string? Version { get; set; }

        [JsonPropertyName("ignore_phrases")]
        public List<string>? IgnorePhrases { get; set; }

        public List<GlossaryEntry>? Entries { get; set; }

    }

    private sealed class GlossaryEntry
    {
        public string? Category { get; set; }

        [JsonPropertyName("zh_cn")]
        public string? ZhCn { get; set; }

        public List<string>? Terms { get; set; }
    }

}
