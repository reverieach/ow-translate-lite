using System.Security.Cryptography;
using System.Text;
using OwTranslateLite.Ocr;
using OwTranslateLite.Translation;

namespace OwTranslateLite.Core;

public sealed class TranslationCoordinator
{
    private readonly AppSettings _settings;
    private readonly OwGlossaryService _glossary;
    private readonly OwChatParser _parser;
    private readonly Dictionary<string, DateTime> _recentHashes = new();
    private HashSet<string> _visibleLastFrame = new(StringComparer.Ordinal);

    public TranslationCoordinator(AppSettings settings, OwGlossaryService glossary)
    {
        _settings = settings;
        _glossary = glossary;
        _parser = new OwChatParser(glossary);
    }

    public async Task<IReadOnlyList<TranslationRecord>> ProcessAsync(IOcrEngine ocrEngine, CancellationToken cancellationToken)
    {
        if (_settings.CaptureRegion is null)
        {
            return Array.Empty<TranslationRecord>();
        }

        using System.Drawing.Bitmap bitmap = ScreenCaptureService.Capture(_settings.CaptureRegion.ToRect());
        IReadOnlyList<OcrTextLine> ocrLines = await ocrEngine.RecognizeAsync(bitmap, _settings.OcrLanguage, cancellationToken);
        IReadOnlyList<ParsedChatLine> chatLines = _parser.Parse(ocrLines);
        if (chatLines.Count == 0)
        {
            return Array.Empty<TranslationRecord>();
        }

        ITranslationProvider provider = TranslationProviderFactory.Create(_settings, _glossary);
        List<TranslationRecord> records = [];
        HashSet<string> visibleThisFrame = new(StringComparer.Ordinal);
        foreach (ParsedChatLine line in chatLines)
        {
            string hash = Hash(CreateMessageKey(line));
            visibleThisFrame.Add(hash);

            if (_visibleLastFrame.Contains(hash) || IsRecentDuplicate(hash))
            {
                continue;
            }

            string translated = await provider.TranslateAsync(line, cancellationToken);
            if (!string.IsNullOrWhiteSpace(translated))
            {
                records.Add(new TranslationRecord(line.Speaker, line.SourceText, translated, DateTime.Now));
                _recentHashes[hash] = DateTime.Now;
            }
        }

        _visibleLastFrame = visibleThisFrame;
        CleanupHashes();
        return records;
    }

    private bool IsRecentDuplicate(string hash)
    {
        return _recentHashes.TryGetValue(hash, out DateTime lastSeen) &&
               DateTime.Now - lastSeen < TimeSpan.FromSeconds(120);
    }

    private void CleanupHashes()
    {
        foreach (string key in _recentHashes.Where(pair => DateTime.Now - pair.Value > TimeSpan.FromMinutes(5)).Select(pair => pair.Key).ToList())
        {
            _recentHashes.Remove(key);
        }
    }

    private static string CreateMessageKey(ParsedChatLine line)
    {
        string speaker = NormalizeForHash(line.Speaker);
        string text = NormalizeForHash(line.SourceText);
        return $"{speaker}:{text}";
    }

    private static string NormalizeForHash(string value)
    {
        string lower = value.ToLowerInvariant();
        lower = System.Text.RegularExpressions.Regex.Replace(lower, @"[^\p{L}\p{N}]+", " ");
        return System.Text.RegularExpressions.Regex.Replace(lower, @"\s+", " ").Trim();
    }

    private static string Hash(string value)
    {
        byte[] data = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(data);
    }
}
