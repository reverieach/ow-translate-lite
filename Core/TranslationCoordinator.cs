using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using OwTranslateLite.Ocr;
using OwTranslateLite.Translation;

namespace OwTranslateLite.Core;

public sealed class TranslationCoordinator
{
    private readonly AppSettings _settings;
    private readonly OwGlossaryService _glossary;
    private readonly OwChatParser _parser;
    private readonly Action<string>? _dedupeLog;
    private readonly ChatTimeline _timeline = new();
    private readonly TimelineAlignmentDetector _alignmentDetector = new();
    private long _frameId;

    public string? ScreenshotSaveDirectory { get; set; }
    public FrameSequenceRecorder? FrameSequenceRecorder { get; set; }
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
        _timeline.Clear();
        _alignmentDetector.Reset();
        _frameId = 0;
        ChatCycleJustReset = false;
        HasVisibleChat = false;
        LastVisibleChatLines = Array.Empty<ParsedChatLine>();
        LogDedupe($"reset-timeline clearRecent={clearRecent}");
    }

    public void ClearPendingTranslations()
    {
        foreach (ChatMessage message in _timeline.Messages)
        {
            if (message.State is ChatMessageState.Queued or ChatMessageState.Translating)
            {
                message.State = ChatMessageState.Confirming;
            }
        }

        LogDedupe("clear-pending-translations timeline");
    }

    public void ReleasePendingTranslations(IReadOnlyList<ParsedChatLine> lines)
    {
        foreach (ParsedChatLine line in lines)
        {
            ChatMessage? message = FindQueuedMessage(line);
            if (message is not null)
            {
                message.State = ChatMessageState.Confirming;
                LogDedupe($"release-pending seq={message.Seq} line={FormatLine(line)}");
            }
        }
    }

    public IReadOnlyList<ParsedChatLine> MarkTranslationFailedForRetry(IReadOnlyList<ParsedChatLine> lines, int maxRetries)
    {
        List<ParsedChatLine> retryLines = [];
        foreach (ParsedChatLine line in lines)
        {
            ChatMessage? message = FindTimelineMessage(line);
            if (message is null)
            {
                LogDedupe($"translation-failed-no-timeline line={FormatLine(line)}");
                continue;
            }

            _timeline.MarkFailed(message);
            if (message.RetryCount <= maxRetries)
            {
                _timeline.MarkQueued(message);
                retryLines.Add(ToParsedChatLine(message));
                LogDedupe($"translation-retry seq={message.Seq} attempt={message.RetryCount} line={FormatLine(line)}");
            }
            else
            {
                LogDedupe($"translation-failed-final seq={message.Seq} retries={message.RetryCount} line={FormatLine(line)}");
            }
        }

        return retryLines;
    }

    public void CompleteOfflineTranslations(IReadOnlyList<ParsedChatLine> lines)
    {
        foreach (ParsedChatLine line in lines)
        {
            ChatMessage? message = FindQueuedMessage(line);
            if (message is not null)
            {
                _timeline.MarkTranslated(message, line.SourceText);
                LogDedupe($"offline-translated seq={message.Seq} line={FormatLine(line)}");
            }
        }
    }

    public async Task<IReadOnlyList<TranslationRecord>> ProcessAsync(IOcrEngine ocrEngine, CancellationToken cancellationToken)
    {
        IReadOnlyList<ParsedChatLine> lines = await DetectNewLinesAsync(ocrEngine, cancellationToken);
        return await TranslateAsync(lines, cancellationToken);
    }

    public async Task<IReadOnlyList<ParsedChatLine>> DetectNewLinesAsync(IOcrEngine ocrEngine, CancellationToken cancellationToken)
    {
        if (_settings.CaptureRegion is null)
        {
            LogDedupe("detect skipped: no capture region");
            return Array.Empty<ParsedChatLine>();
        }

        System.Windows.Rect captureRegion = _settings.CaptureRegion.ToRect();
        using Bitmap bitmap = ScreenCaptureService.Capture(captureRegion);
        return await DetectNewLinesFromBitmapAsync(ocrEngine, bitmap, captureRegion, cancellationToken);
    }

    public async Task<IReadOnlyList<ParsedChatLine>> DetectNewLinesFromBitmapAsync(
        IOcrEngine ocrEngine,
        Bitmap bitmap,
        System.Windows.Rect captureRegion,
        CancellationToken cancellationToken)
    {
        ChatCycleJustReset = false;
        HasVisibleChat = false;
        IReadOnlyList<OcrTextLine> rawOcrLines = await ocrEngine.RecognizeAsync(bitmap, _settings.OcrLanguage, cancellationToken);
        IReadOnlyList<OcrTextLine> processedOcrLines = OcrTextPostProcessor.Process(rawOcrLines);
        IReadOnlyList<ParsedChatLine> chatLines = _parser.Parse(processedOcrLines);
        FrameDetectionResult detectionResult = DetectNewLinesFromParsedLines(chatLines);
        FrameSequenceRecorder?.RecordFrame(
            bitmap,
            captureRegion,
            rawOcrLines,
            processedOcrLines,
            chatLines,
            detectionResult);

        IReadOnlyList<ParsedChatLine> newLines = detectionResult.NewLines;
        if (newLines.Count > 0 && ScreenshotSaveDirectory is not null)
        {
            try
            {
                Directory.CreateDirectory(ScreenshotSaveDirectory);
                string file = Path.Combine(ScreenshotSaveDirectory, $"cap_{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
                bitmap.Save(file, ImageFormat.Png);
            }
            catch
            {
                // Best-effort: don't disrupt the OCR pipeline if saving fails.
            }
        }

        return newLines;
    }

    public FrameDetectionResult DetectNewLinesFromParsedLines(IReadOnlyList<ParsedChatLine> chatLines)
    {
        ChatCycleJustReset = false;
        HasVisibleChat = chatLines.Count > 0;
        LastVisibleChatLines = chatLines;

        long frameId = ++_frameId;
        TimelineAlignmentResult alignment = _alignmentDetector.Detect(_timeline, chatLines, frameId);
        List<ParsedChatLine> confirmedLines = [];
        List<FrameDetectionDecision> decisions = [];

        foreach (TimelineAlignmentMatch match in alignment.Matches)
        {
            string reason = $"matched-seq-{match.Message.Seq}-score-{match.Score:0.###}";
            decisions.Add(new FrameDetectionDecision(match.Line, false, reason, match.Message.Seq.ToString()));
        }

        foreach (ChatMessage message in alignment.NewMessages)
        {
            message.State = ChatMessageState.Confirming;
            ParsedChatLine line = ToParsedChatLine(message);
            decisions.Add(new FrameDetectionDecision(line, false, $"confirming-seq-{message.Seq}", message.Seq.ToString()));
        }

        foreach (ChatMessage message in _timeline.Messages)
        {
            if (message.State is not (ChatMessageState.Detected or ChatMessageState.Confirming))
            {
                continue;
            }

            if (message.LastSeenFrameId != frameId)
            {
                continue;
            }

            if (!message.IsReadyForTranslation())
            {
                ParsedChatLine waitingLine = ToParsedChatLine(message);
                decisions.Add(new FrameDetectionDecision(
                    waitingLine,
                    false,
                    $"confirming-seq-{message.Seq}-seen-{message.SeenCount}",
                    message.Seq.ToString()));
                continue;
            }

            _timeline.MarkQueued(message);
            ParsedChatLine line = ToParsedChatLine(message);
            confirmedLines.Add(line);
            decisions.Add(new FrameDetectionDecision(line, true, $"queued-seq-{message.Seq}-seen-{message.SeenCount}", message.Seq.ToString()));
        }

        LogDedupe(
            $"timeline-frame id={frameId} visible={chatLines.Count} reason={alignment.Reason} matches={alignment.Matches.Count} new={alignment.NewMessages.Count} queued={confirmedLines.Count}");
        return new FrameDetectionResult(
            chatLines,
            chatLines,
            decisions,
            confirmedLines,
            HasVisibleChat,
            ChatCycleJustReset);
    }

    public async Task<IReadOnlyList<TranslationRecord>> TranslateAsync(IReadOnlyList<ParsedChatLine> newLines, CancellationToken cancellationToken)
    {
        if (newLines.Count == 0)
        {
            return Array.Empty<TranslationRecord>();
        }

        foreach (ParsedChatLine line in newLines)
        {
            ChatMessage? message = FindQueuedMessage(line);
            if (message is not null)
            {
                _timeline.MarkTranslating(message);
            }
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

                ChatMessage? message = FindTimelineMessage(result.SourceLine);
                long seq = message?.Seq ?? 0;
                records.Add(new TranslationRecord(
                    seq,
                    result.SourceLine.Speaker,
                    result.SourceLine.SourceText,
                    result.TranslatedText,
                    DateTime.Now));
                if (message is not null)
                {
                    _timeline.MarkTranslated(message, result.TranslatedText);
                    LogDedupe($"translated seq={message.Seq} line={FormatLine(result.SourceLine)}");
                }
            }

            return records;
        }
        finally
        {
            LogDedupe($"translate-finally count={newLines.Count}");
        }
    }

    private ChatMessage? FindQueuedMessage(ParsedChatLine line)
    {
        if (line.Seq != 0)
        {
            // Identity match: never fall back to fuzzy text for seq-carrying lines, otherwise two
            // near-identical messages collide (one translated twice, the other stuck forever).
            ChatMessage? bySeq = _timeline.Messages.FirstOrDefault(message => message.Seq == line.Seq);
            return bySeq is not null && bySeq.State is ChatMessageState.Queued or ChatMessageState.Translating
                ? bySeq
                : null;
        }

        return _timeline.Messages
            .LastOrDefault(message =>
                message.State is ChatMessageState.Queued or ChatMessageState.Translating &&
                IsSameTimelineLine(message, line));
    }

    private ChatMessage? FindTimelineMessage(ParsedChatLine line) =>
        line.Seq != 0
            ? _timeline.Messages.FirstOrDefault(message => message.Seq == line.Seq)
            : _timeline.Messages.LastOrDefault(message => IsSameTimelineLine(message, line));

    private static bool IsSameTimelineLine(ChatMessage message, ParsedChatLine line)
    {
        string messageSpeaker = OcrDedupeNormalizer.NormalizeSpeaker(message.Speaker);
        string lineSpeaker = OcrDedupeNormalizer.NormalizeSpeaker(line.Speaker);
        if (!OcrDedupeNormalizer.IsSpeakerMatch(messageSpeaker, lineSpeaker))
        {
            return false;
        }

        string messageText = OcrDedupeNormalizer.NormalizeText(message.ConsensusText);
        string lineText = OcrDedupeNormalizer.NormalizeText(line.SourceText);
        return OcrDedupeNormalizer.TextSimilarityScore(messageText, lineText) >= 0.76;
    }

    private static ParsedChatLine ToParsedChatLine(ChatMessage message) =>
        new(message.Speaker, message.ConsensusText, message.Bounds, message.GlossaryHits) { Seq = message.Seq };

    private void LogDedupe(string message)
    {
        if (_settings.EnableDedupeDebugLog)
        {
            _dedupeLog?.Invoke(message);
        }
    }

    private static string FormatLine(ParsedChatLine line) =>
        $"{Limit(line.Speaker, 24)}:{Limit(line.SourceText, 80)}";

    private static string Limit(string value, int maxLength)
    {
        string trimmed = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..maxLength] + "...";
    }
}
