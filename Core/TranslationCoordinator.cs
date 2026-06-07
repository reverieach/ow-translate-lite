using OwTranslateLite.Ocr;
using OwTranslateLite.Translation;

namespace OwTranslateLite.Core;

public sealed class TranslationCoordinator
{
    private readonly AppSettings _settings;
    private readonly OwGlossaryService _glossary;
    private readonly OwChatParser _parser;
    private readonly Action<string>? _dedupeLog;
    private readonly List<VisibleMessageSnapshot> _previousVisibleMessages = [];
    private readonly HashSet<string> _seenInCurrentChatCycle = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingMessageKeys = new(StringComparer.Ordinal);
    private readonly List<VisibleMessageSnapshot> _pendingMessageSnapshots = [];
    private readonly Dictionary<string, DateTime> _recentDedupeCache = new(StringComparer.Ordinal);
    private readonly List<RecentMessageSnapshot> _recentMessageSnapshots = [];
    private DateTime? _lastAnyMessageVisibleAt;
    private static readonly TimeSpan ChatHiddenReset = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ShortRecentDedupeTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultRecentDedupeTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LongRecentDedupeTtl = TimeSpan.FromSeconds(90);
    private const int MaxRecentDedupeItems = 500;
    private const int MaxTailMessagesWithoutAnchor = 2;
    private const double AnchorScoreThreshold = 0.82;
    private const double DuplicateTextScoreThreshold = 0.76;

    public bool ChatCycleJustReset { get; private set; }
    public bool HasVisibleChat { get; private set; }
    public IReadOnlyList<ParsedChatLine> LastVisibleChatLines { get; private set; } = Array.Empty<ParsedChatLine>();

    public TranslationCoordinator(AppSettings settings, OwGlossaryService glossary, Action<string>? dedupeLog = null)
    {
        _settings = settings;
        _glossary = glossary;
        _dedupeLog = dedupeLog;
        _parser = new OwChatParser(glossary);
    }

    public void ResetChatCycle(bool clearRecent = false)
    {
        _previousVisibleMessages.Clear();
        _seenInCurrentChatCycle.Clear();
        _pendingMessageKeys.Clear();
        _pendingMessageSnapshots.Clear();
        _lastAnyMessageVisibleAt = null;
        ChatCycleJustReset = false;
        HasVisibleChat = false;
        LogDedupe($"reset-cycle clearRecent={clearRecent}");
        if (clearRecent)
        {
            _recentDedupeCache.Clear();
            _recentMessageSnapshots.Clear();
        }
    }

    public void ClearPendingTranslations()
    {
        _pendingMessageKeys.Clear();
        _pendingMessageSnapshots.Clear();
        LogDedupe("clear-pending-translations");
    }

    public void ReleasePendingTranslations(IReadOnlyList<ParsedChatLine> lines)
    {
        foreach (ParsedChatLine line in lines)
        {
            _pendingMessageKeys.Remove(CreateMessageKey(line));
            RemovePendingSnapshot(CreateSnapshot(line));
        }

        if (lines.Count > 0)
        {
            LogDedupe($"release-pending count={lines.Count} lines={FormatLines(lines)}");
        }
    }

    public async Task<IReadOnlyList<TranslationRecord>> ProcessAsync(IOcrEngine ocrEngine, CancellationToken cancellationToken)
    {
        IReadOnlyList<ParsedChatLine> lines = await DetectNewLinesAsync(ocrEngine, cancellationToken);
        return await TranslateAsync(lines, cancellationToken);
    }

    public async Task<IReadOnlyList<ParsedChatLine>> DetectNewLinesAsync(IOcrEngine ocrEngine, CancellationToken cancellationToken)
    {
        ChatCycleJustReset = false;
        HasVisibleChat = false;
        if (_settings.CaptureRegion is null)
        {
            LogDedupe("detect skipped: no capture region");
            return Array.Empty<ParsedChatLine>();
        }

        System.Windows.Rect captureRegion = _settings.CaptureRegion.ToRect();
        using System.Drawing.Bitmap bitmap = ScreenCaptureService.Capture(captureRegion);
        IReadOnlyList<OcrTextLine> ocrLines = await ocrEngine.RecognizeAsync(bitmap, _settings.OcrLanguage, cancellationToken);
        ocrLines = OcrTextPostProcessor.Process(ocrLines);
        IReadOnlyList<ParsedChatLine> chatLines = _parser.Parse(ocrLines);
        LastVisibleChatLines = chatLines;
        LogDedupe($"ocr-frame ocrLines={ocrLines.Count} chatLines={chatLines.Count} previous={_previousVisibleMessages.Count} visible={FormatLines(chatLines)}");
        if (chatLines.Count == 0)
        {
            ChatCycleJustReset = ResetCycleIfChatStayedHidden();
            LogDedupe($"no-chat-lines reset={ChatCycleJustReset}");
            return Array.Empty<ParsedChatLine>();
        }

        HasVisibleChat = true;
        DateTime now = DateTime.Now;
        CleanupRecentDedupe(now);
        _lastAnyMessageVisibleAt = now;
        List<ParsedChatLine> candidateLines = FindNewLinesByVisibleOrder(chatLines);
        LogDedupe($"candidate-lines count={candidateLines.Count} candidates={FormatLines(candidateLines)}");
        UpdatePreviousVisibleMessages(chatLines);

        List<ParsedChatLine> newLines = [];
        HashSet<string> batchKeys = new(StringComparer.Ordinal);
        foreach (ParsedChatLine line in candidateLines)
        {
            string key = CreateMessageKey(line);
            VisibleMessageSnapshot snapshot = CreateSnapshot(line);
            string? duplicateReason = GetDuplicateReason(key, snapshot, line.SourceText, now, batchKeys);
            if (duplicateReason is not null)
            {
                LogDedupe($"drop reason={duplicateReason} line={FormatLine(line)} key={key}");
                continue;
            }

            newLines.Add(line);
            _pendingMessageKeys.Add(key);
            _pendingMessageSnapshots.Add(snapshot);
            LogDedupe($"accept line={FormatLine(line)} key={key}");
        }

        LogDedupe($"new-lines count={newLines.Count}");
        return newLines;
    }

    public async Task<IReadOnlyList<TranslationRecord>> TranslateAsync(IReadOnlyList<ParsedChatLine> newLines, CancellationToken cancellationToken)
    {
        if (newLines.Count == 0)
        {
            return Array.Empty<TranslationRecord>();
        }

        try
        {
            ITranslationProvider provider = TranslationProviderFactory.Create(_settings, _glossary);
            IReadOnlyList<TranslationResult> translations = await provider.TranslateAsync(newLines, cancellationToken);
            List<TranslationRecord> records = [];
            foreach (TranslationResult result in translations)
            {
                if (string.IsNullOrWhiteSpace(result.TranslatedText))
                {
                    continue;
                }

                records.Add(new TranslationRecord(
                    result.SourceLine.Speaker,
                    result.SourceLine.SourceText,
                    result.TranslatedText,
                    DateTime.Now));
                string key = CreateMessageKey(result.SourceLine);
                VisibleMessageSnapshot snapshot = CreateSnapshot(result.SourceLine);
                _seenInCurrentChatCycle.Add(key);
                _recentDedupeCache[key] = DateTime.Now;
                _recentMessageSnapshots.Add(new RecentMessageSnapshot(snapshot, DateTime.Now));
                LogDedupe($"translated remembered line={FormatLine(result.SourceLine)} key={key}");
            }

            return records;
        }
        finally
        {
            foreach (ParsedChatLine line in newLines)
            {
                _pendingMessageKeys.Remove(CreateMessageKey(line));
                RemovePendingSnapshot(CreateSnapshot(line));
            }

            LogDedupe($"translate-finally released count={newLines.Count}");
        }
    }

    private bool ResetCycleIfChatStayedHidden()
    {
        if (_lastAnyMessageVisibleAt is null)
        {
            return false;
        }

        if (DateTime.Now - _lastAnyMessageVisibleAt.Value >= ChatHiddenReset)
        {
            _previousVisibleMessages.Clear();
            _seenInCurrentChatCycle.Clear();
            _pendingMessageKeys.Clear();
            _pendingMessageSnapshots.Clear();
            _lastAnyMessageVisibleAt = null;
            LogDedupe("chat-hidden-reset");
            return true;
        }

        return false;
    }

    private List<ParsedChatLine> FindNewLinesByVisibleOrder(IReadOnlyList<ParsedChatLine> currentLines)
    {
        if (_previousVisibleMessages.Count == 0)
        {
            LogDedupe($"order no-previous take-tail count={Math.Min(currentLines.Count, MaxTailMessagesWithoutAnchor)}");
            return TakeUnanchoredTail(currentLines);
        }

        List<VisibleMessageSnapshot> current = currentLines.Select(CreateSnapshot).ToList();
        int anchorIndex = FindBestAnchorIndex(current);
        if (anchorIndex >= 0)
        {
            LogDedupe($"order anchor-index={anchorIndex} newAfter={currentLines.Count - anchorIndex - 1} anchor={FormatSnapshot(current[anchorIndex])}");
            return currentLines.Skip(anchorIndex + 1).ToList();
        }

        LogDedupe($"order no-anchor take-tail count={Math.Min(currentLines.Count, MaxTailMessagesWithoutAnchor)}");
        return TakeUnanchoredTail(currentLines);
    }

    private static List<ParsedChatLine> TakeUnanchoredTail(IReadOnlyList<ParsedChatLine> currentLines)
    {
        if (currentLines.Count <= MaxTailMessagesWithoutAnchor)
        {
            return currentLines.ToList();
        }

        return currentLines.TakeLast(MaxTailMessagesWithoutAnchor).ToList();
    }

    private int FindBestAnchorIndex(IReadOnlyList<VisibleMessageSnapshot> current)
    {
        int bestIndex = -1;
        double bestScore = 0;
        for (int currentIndex = current.Count - 1; currentIndex >= 0; currentIndex--)
        {
            VisibleMessageSnapshot currentMessage = current[currentIndex];
            for (int previousIndex = _previousVisibleMessages.Count - 1; previousIndex >= 0; previousIndex--)
            {
                double score = GetAnchorScore(current, currentIndex, previousIndex);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = currentIndex;
                }
            }
        }

        if (bestIndex >= 0 && bestScore >= AnchorScoreThreshold)
        {
            LogDedupe($"anchor best index={bestIndex} score={bestScore:0.###} current={FormatSnapshot(current[bestIndex])}");
            return bestIndex;
        }

        LogDedupe($"anchor none bestScore={bestScore:0.###}");
        return -1;
    }

    private double GetAnchorScore(IReadOnlyList<VisibleMessageSnapshot> current, int currentIndex, int previousIndex)
    {
        VisibleMessageSnapshot currentMessage = current[currentIndex];
        VisibleMessageSnapshot previousMessage = _previousVisibleMessages[previousIndex];
        if (!OcrDedupeNormalizer.IsSpeakerMatch(currentMessage.NormalizedSpeaker, previousMessage.NormalizedSpeaker))
        {
            return 0;
        }

        double textScore = OcrDedupeNormalizer.TextSimilarityScore(currentMessage.NormalizedText, previousMessage.NormalizedText);
        if (textScore < DuplicateTextScoreThreshold)
        {
            return 0;
        }

        double score = textScore;
        if (currentMessage.NormalizedSpeaker == previousMessage.NormalizedSpeaker)
        {
            score += 0.04;
        }

        score += GetNeighborSupportScore(current, currentIndex, previousIndex);
        if (currentMessage.NormalizedText.Length >= 12)
        {
            score += 0.03;
        }

        return Math.Min(1, score);
    }

    private double GetNeighborSupportScore(IReadOnlyList<VisibleMessageSnapshot> current, int currentIndex, int previousIndex)
    {
        double support = 0;
        if (currentIndex > 0 && previousIndex > 0)
        {
            support += GetLooseNeighborScore(current[currentIndex - 1], _previousVisibleMessages[previousIndex - 1]) * 0.08;
        }

        if (currentIndex + 1 < current.Count && previousIndex + 1 < _previousVisibleMessages.Count)
        {
            support += GetLooseNeighborScore(current[currentIndex + 1], _previousVisibleMessages[previousIndex + 1]) * 0.08;
        }

        return support;
    }

    private static double GetLooseNeighborScore(VisibleMessageSnapshot left, VisibleMessageSnapshot right)
    {
        if (!OcrDedupeNormalizer.IsSpeakerMatch(left.NormalizedSpeaker, right.NormalizedSpeaker))
        {
            return 0;
        }

        return OcrDedupeNormalizer.TextSimilarityScore(left.NormalizedText, right.NormalizedText);
    }

    private static bool IsAnchorMatch(VisibleMessageSnapshot left, VisibleMessageSnapshot right)
    {
        if (!OcrDedupeNormalizer.IsSpeakerMatch(left.NormalizedSpeaker, right.NormalizedSpeaker))
        {
            return false;
        }

        return OcrDedupeNormalizer.TextSimilarityScore(left.NormalizedText, right.NormalizedText) >= DuplicateTextScoreThreshold;
    }

    private void UpdatePreviousVisibleMessages(IReadOnlyList<ParsedChatLine> currentLines)
    {
        _previousVisibleMessages.Clear();
        _previousVisibleMessages.AddRange(currentLines.Select(CreateSnapshot));
    }

    private static VisibleMessageSnapshot CreateSnapshot(ParsedChatLine line) =>
        new(NormalizeSpeakerForHash(line.Speaker), NormalizeForHash(line.SourceText));

    private bool IsPendingDuplicate(VisibleMessageSnapshot snapshot) =>
        _pendingMessageSnapshots.Any(pending => IsAnchorMatch(snapshot, pending));

    private bool IsRecentDuplicate(string key, VisibleMessageSnapshot snapshot, string text, DateTime now)
    {
        TimeSpan ttl = GetRecentDedupeTtl(text);
        if (_recentDedupeCache.TryGetValue(key, out DateTime lastSeenAt) &&
            now - lastSeenAt < ttl)
        {
            return true;
        }

        return _recentMessageSnapshots.Any(recent =>
            now - recent.SeenAt < ttl &&
            IsAnchorMatch(snapshot, recent.Message));
    }

    private string? GetDuplicateReason(
        string key,
        VisibleMessageSnapshot snapshot,
        string text,
        DateTime now,
        HashSet<string> batchKeys)
    {
        if (_seenInCurrentChatCycle.Contains(key))
        {
            return "seen-current-cycle";
        }

        if (_pendingMessageKeys.Contains(key))
        {
            return "pending-key";
        }

        if (IsPendingDuplicate(snapshot))
        {
            return "pending-similar";
        }

        if (IsRecentDuplicate(key, snapshot, text, now))
        {
            return $"recent-ttl-{GetRecentDedupeTtl(text).TotalSeconds:0}s";
        }

        if (!batchKeys.Add(key))
        {
            return "same-frame-duplicate";
        }

        return null;
    }

    private void CleanupRecentDedupe(DateTime now)
    {
        foreach (KeyValuePair<string, DateTime> item in _recentDedupeCache.ToList())
        {
            if (now - item.Value >= LongRecentDedupeTtl)
            {
                _recentDedupeCache.Remove(item.Key);
            }
        }

        _recentMessageSnapshots.RemoveAll(item => now - item.SeenAt >= LongRecentDedupeTtl);

        if (_recentDedupeCache.Count <= MaxRecentDedupeItems)
        {
            TrimRecentSnapshots();
            return;
        }

        foreach (string key in _recentDedupeCache
                     .OrderBy(item => item.Value)
                     .Take(_recentDedupeCache.Count - MaxRecentDedupeItems)
                     .Select(item => item.Key)
                     .ToList())
        {
            _recentDedupeCache.Remove(key);
        }

        TrimRecentSnapshots();
    }

    private void TrimRecentSnapshots()
    {
        if (_recentMessageSnapshots.Count <= MaxRecentDedupeItems)
        {
            return;
        }

        _recentMessageSnapshots.RemoveRange(0, _recentMessageSnapshots.Count - MaxRecentDedupeItems);
    }

    private void RemovePendingSnapshot(VisibleMessageSnapshot snapshot)
    {
        int index = _pendingMessageSnapshots.FindIndex(pending => IsAnchorMatch(snapshot, pending));
        if (index >= 0)
        {
            _pendingMessageSnapshots.RemoveAt(index);
        }
    }

    private static TimeSpan GetRecentDedupeTtl(string text)
    {
        int length = NormalizeForHash(text).Length;
        return length switch
        {
            <= 12 => ShortRecentDedupeTtl,
            >= 50 => LongRecentDedupeTtl,
            _ => DefaultRecentDedupeTtl
        };
    }

    private static string CreateMessageKey(ParsedChatLine line) =>
        $"{NormalizeSpeakerForHash(line.Speaker)}:{NormalizeForHash(line.SourceText)}";

    private static string NormalizeForHash(string value)
        => OcrDedupeNormalizer.NormalizeText(value);

    private static string NormalizeSpeakerForHash(string value)
        => OcrDedupeNormalizer.NormalizeSpeaker(value);

    private void LogDedupe(string message)
    {
        if (_settings.EnableDedupeDebugLog)
        {
            _dedupeLog?.Invoke(message);
        }
    }

    private static string FormatLines(IReadOnlyList<ParsedChatLine> lines) =>
        lines.Count == 0
            ? "[]"
            : "[" + string.Join(" | ", lines.Select(FormatLine)) + "]";

    private static string FormatLine(ParsedChatLine line) =>
        $"{Limit(line.Speaker, 24)}:{Limit(line.SourceText, 80)}";

    private static string FormatSnapshot(VisibleMessageSnapshot snapshot) =>
        $"{Limit(snapshot.NormalizedSpeaker, 24)}:{Limit(snapshot.NormalizedText, 80)}";

    private static string Limit(string value, int maxLength)
    {
        string trimmed = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..maxLength] + "...";
    }

    private sealed record VisibleMessageSnapshot(string NormalizedSpeaker, string NormalizedText);

    private sealed record RecentMessageSnapshot(VisibleMessageSnapshot Message, DateTime SeenAt);
}
