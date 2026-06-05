using OwTranslateLite.Ocr;
using OwTranslateLite.Translation;

namespace OwTranslateLite.Core;

public sealed class TranslationCoordinator
{
    private readonly AppSettings _settings;
    private readonly OwGlossaryService _glossary;
    private readonly OwChatParser _parser;
    private readonly HashSet<string> _seenInCurrentChatCycle = new(StringComparer.Ordinal);
    private DateTime? _lastAnyMessageVisibleAt;
    private static readonly TimeSpan ChatHiddenReset = TimeSpan.FromSeconds(3);

    public bool ChatCycleJustReset { get; private set; }

    public TranslationCoordinator(AppSettings settings, OwGlossaryService glossary)
    {
        _settings = settings;
        _glossary = glossary;
        _parser = new OwChatParser(glossary);
    }

    public async Task<IReadOnlyList<TranslationRecord>> ProcessAsync(IOcrEngine ocrEngine, CancellationToken cancellationToken)
    {
        ChatCycleJustReset = false;
        if (_settings.CaptureRegion is null)
        {
            return Array.Empty<TranslationRecord>();
        }

        System.Windows.Rect captureRegion = _settings.CaptureRegion.ToRect();
        using System.Drawing.Bitmap bitmap = ScreenCaptureService.Capture(captureRegion);
        IReadOnlyList<OcrTextLine> ocrLines = await ocrEngine.RecognizeAsync(bitmap, _settings.OcrLanguage, cancellationToken);
        IReadOnlyList<ParsedChatLine> chatLines = _parser.Parse(ocrLines);
        if (chatLines.Count == 0)
        {
            ChatCycleJustReset = ResetCycleIfChatStayedHidden();
            return Array.Empty<TranslationRecord>();
        }

        _lastAnyMessageVisibleAt = DateTime.Now;
        ITranslationProvider provider = TranslationProviderFactory.Create(_settings, _glossary);
        List<TranslationRecord> records = [];
        foreach (ParsedChatLine line in chatLines)
        {
            string key = CreateMessageKey(line);
            if (_seenInCurrentChatCycle.Contains(key))
            {
                continue;
            }

            string translated = await provider.TranslateAsync(line, cancellationToken);
            if (!string.IsNullOrWhiteSpace(translated))
            {
                System.Windows.Rect screenBounds = new(
                    captureRegion.Left + line.Bounds.Left,
                    captureRegion.Top + line.Bounds.Top,
                    line.Bounds.Width,
                    line.Bounds.Height);
                records.Add(new TranslationRecord(line.Speaker, line.SourceText, translated, screenBounds, DateTime.Now));
                _seenInCurrentChatCycle.Add(key);
            }
        }

        return records;
    }

    private bool ResetCycleIfChatStayedHidden()
    {
        if (_lastAnyMessageVisibleAt is null)
        {
            return false;
        }

        if (DateTime.Now - _lastAnyMessageVisibleAt.Value >= ChatHiddenReset)
        {
            _seenInCurrentChatCycle.Clear();
            _lastAnyMessageVisibleAt = null;
            return true;
        }

        return false;
    }

    private static string CreateMessageKey(ParsedChatLine line) =>
        $"{NormalizeForHash(line.Speaker)}:{NormalizeForHash(line.SourceText)}";

    private static string NormalizeForHash(string value)
    {
        string lower = value.ToLowerInvariant();
        lower = System.Text.RegularExpressions.Regex.Replace(lower, @"[^\p{L}\p{N}]+", " ");
        return System.Text.RegularExpressions.Regex.Replace(lower, @"\s+", " ").Trim();
    }
}
