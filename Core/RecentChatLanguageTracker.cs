using System.Text.RegularExpressions;

namespace OwTranslateLite.Core;

public sealed class RecentChatLanguageTracker
{
    private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(2);
    private const int MaxEntries = 50;
    private readonly Queue<LanguageSample> _samples = [];
    private readonly object _lock = new();

    public void Record(IReadOnlyList<ParsedChatLine> lines)
    {
        DateTime now = DateTime.Now;
        lock (_lock)
        {
            foreach (ParsedChatLine line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line.SourceText))
                {
                    _samples.Enqueue(new LanguageSample(line.SourceText, now));
                }
            }

            Trim(now);
        }
    }

    public string DetectOrDefault(string fallback = "en")
    {
        lock (_lock)
        {
            Trim(DateTime.Now);
            int english = 0;
            int japanese = 0;
            int korean = 0;

            foreach (LanguageSample sample in _samples)
            {
                string text = sample.Text;
                english += Regex.Matches(text, @"[A-Za-z]").Count;
                japanese += Regex.Matches(text, @"[\u3040-\u30ff]").Count * 3;
                korean += Regex.Matches(text, @"[\uac00-\ud7af]").Count * 3;
            }

            if (korean > japanese && korean > english)
            {
                return "ko";
            }

            if (japanese > korean && japanese > english)
            {
                return "ja";
            }

            if (english > 0)
            {
                return "en";
            }

            return fallback;
        }
    }

    private void Trim(DateTime now)
    {
        while (_samples.Count > 0 && now - _samples.Peek().SeenAt > EntryTtl)
        {
            _samples.Dequeue();
        }

        while (_samples.Count > MaxEntries)
        {
            _samples.Dequeue();
        }
    }

    private sealed record LanguageSample(string Text, DateTime SeenAt);
}
