using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OwTranslateLite.Core;

public sealed class OwGlossaryService
{
    private readonly List<GlossaryEntry> _entries = [];
    private readonly List<RewriteRule> _rewrites = [];
    private readonly List<string> _ignorePhrases = [];
    private readonly Dictionary<string, GlossaryEntry> _normalizedTermMap = new(StringComparer.OrdinalIgnoreCase);

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
        service._rewrites.AddRange(file.LocalRewrites ?? []);

        foreach (GlossaryEntry entry in service._entries)
        {
            foreach (string term in entry.Terms ?? [])
            {
                string key = NormalizeKey(term);
                if (!service._normalizedTermMap.ContainsKey(key))
                {
                    service._normalizedTermMap[key] = entry;
                }
            }
        }

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

    public string TryLocalTranslate(string text)
    {
        string normalized = NormalizeOcrText(text);
        string withTerms = ApplyTerms(normalized);

        string quick = TryQuickCompetitiveTranslate(normalized);
        if (!string.IsNullOrWhiteSpace(quick))
        {
            return quick;
        }

        foreach (RewriteRule rule in _rewrites)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern) || string.IsNullOrWhiteSpace(rule.ZhCn))
            {
                continue;
            }

            Match match = Regex.Match(normalized, rule.Pattern, RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }

            string output = rule.ZhCn;
            output = output.Replace("{term}", FindFirstTerm(match.Value) ?? withTerms);
            output = output.Replace("{skill}", FindFirstByCategory(match.Value, "ability") ?? "技能");
            return output;
        }

        IReadOnlyList<GlossaryHit> hits = FindHits(normalized);
        if (hits.Count > 0)
        {
            string compact = withTerms;
            compact = Regex.Replace(compact, @"\b(no|なし|없음)\b", "没", RegexOptions.IgnoreCase);
            compact = Regex.Replace(compact, @"\b(focus|フォーカス|점사)\b", "集火", RegexOptions.IgnoreCase);
            compact = Regex.Replace(compact, @"\b(behind|flank|裏|뒤)\b", "绕后", RegexOptions.IgnoreCase);
            compact = Regex.Replace(compact, @"\b(ult|ultimate|ウルト|궁)\b", "开大", RegexOptions.IgnoreCase);
            return compact;
        }

        return normalized;
    }

    private string TryQuickCompetitiveTranslate(string text)
    {
        string raw = text.ToLowerInvariant();
        string normalized = NormalizeKey(text);
        IReadOnlyList<GlossaryHit> hits = FindHits(text);

        if (normalized is "group up" or "regroup" or "group" ||
            raw.Contains("集合して") ||
            raw.Contains("모여") ||
            raw.Contains("뭉쳐"))
        {
            return "集合";
        }

        if (normalized is "hello" or "hi" or "hey" ||
            raw.Contains("안녕") ||
            raw.Contains("こんにちは") ||
            raw.Contains("こんばんは"))
        {
            return "你好";
        }

        if (Regex.IsMatch(normalized, @"\b(heal|heals|healing)\b") ||
            raw.Contains("힐") ||
            raw.Contains("回復") ||
            raw.Contains("ヒール"))
        {
            return "奶我";
        }

        bool hasNano = hits.Any(hit => hit.Target == "纳米激素") ||
                       normalized.Contains("nano") ||
                       raw.Contains("나노");
        bool hasBlade = hits.Any(hit => hit.Target == "龙刃") ||
                        normalized.Contains("blade") ||
                        raw.Contains("블레이드") ||
                        raw.Contains("용검");
        bool soon = Regex.IsMatch(normalized, @"\b(soon|ready|almost)\b") ||
                    raw.Contains("ある") ||
                    raw.Contains("있음") ||
                    raw.Contains("준비");
        if (hasNano && hasBlade && soon)
        {
            return "纳米刀快好了";
        }

        if (hasNano && soon)
        {
            return "纳米激素快好了";
        }

        if (hasBlade && soon)
        {
            return "龙刃快好了";
        }

        bool hasSuzu = hits.Any(hit => hit.Target == "铃") ||
                       normalized.Contains("suzu") ||
                       raw.Contains("스즈") ||
                       raw.Contains("鈴");
        if (hasSuzu && (raw.Contains("빠짐") || raw.Contains("없음") || raw.Contains("なし") || Regex.IsMatch(normalized, @"\b(no|none|used)\b")))
        {
            return "铃没了";
        }

        GlossaryHit? skill = hits.FirstOrDefault(hit => hit.Category.Equals("ability", StringComparison.OrdinalIgnoreCase));
        bool noAfterSkill = Regex.IsMatch(normalized, @"\b(no|none|used)\b") ||
                            raw.Contains("なし") ||
                            raw.Contains("없음") ||
                            raw.Contains("빠짐") ||
                            Regex.IsMatch(normalized, @"\b(suzu|sleep|nade|lamp|deflect|bubble|hook)\s+(no|none|used)\b");
        if (skill is not null && noAfterSkill)
        {
            GlossaryHit? hero = hits.FirstOrDefault(hit => hit.Category.Equals("hero", StringComparison.OrdinalIgnoreCase));
            return hero is null ? $"{skill.Target}没了" : $"{hero.Target}没{skill.Target}";
        }

        GlossaryHit? focusTarget = hits.FirstOrDefault(hit => hit.Category.Equals("hero", StringComparison.OrdinalIgnoreCase));
        if (focusTarget is not null &&
            (Regex.IsMatch(normalized, @"\b(focus|kill|burn|melt)\b") ||
             raw.Contains("점사") ||
             raw.Contains("フォーカス")))
        {
            return $"集火{focusTarget.Target}";
        }

        if (raw.Contains("뒤") || raw.Contains("裏") || Regex.IsMatch(normalized, @"\b(flank|behind|backline)\b"))
        {
            return "有人绕后";
        }

        return "";
    }

    public string BuildPromptContext(IReadOnlyList<GlossaryHit> hits)
    {
        if (hits.Count == 0)
        {
            return "无术语命中。";
        }

        return string.Join("; ", hits.Select(hit => $"{hit.Source}->{hit.Target}({hit.Category})"));
    }

    private string? FindFirstTerm(string text)
    {
        return FindHits(text).FirstOrDefault(hit => hit.Category.Equals("hero", StringComparison.OrdinalIgnoreCase))?.Target
            ?? FindHits(text).FirstOrDefault()?.Target;
    }

    private string? FindFirstByCategory(string text, string category)
    {
        return FindHits(text).FirstOrDefault(hit => hit.Category.Equals(category, StringComparison.OrdinalIgnoreCase))?.Target;
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

        [JsonPropertyName("local_rewrites")]
        public List<RewriteRule>? LocalRewrites { get; set; }
    }

    private sealed class GlossaryEntry
    {
        public string? Category { get; set; }

        [JsonPropertyName("zh_cn")]
        public string? ZhCn { get; set; }

        public List<string>? Terms { get; set; }
    }

    private sealed class RewriteRule
    {
        public string? Pattern { get; set; }

        [JsonPropertyName("zh_cn")]
        public string? ZhCn { get; set; }
    }
}
