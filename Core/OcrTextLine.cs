using System.Windows;

namespace OwTranslateLite.Core;

public sealed record OcrTextLine(string Text, Rect Bounds);

public sealed record ParsedChatLine(string Speaker, string SourceText, Rect Bounds, IReadOnlyList<GlossaryHit> GlossaryHits)
{
    /// <summary>
    /// Timeline message id this line was produced from. 0 means "no identity" (parser/reply paths)
    /// and callers fall back to fuzzy text matching. Carries through the translation queue and the
    /// provider (which preserves the source line reference) so results map back to the exact message
    /// by id instead of by text similarity — two near-identical messages can no longer collide.
    /// </summary>
    public long Seq { get; init; }
}

public sealed record TranslationRecord(long Seq, string Speaker, string SourceText, string TranslatedText, DateTime Timestamp);

public sealed record TranslationResult(ParsedChatLine SourceLine, string TranslatedText);

public sealed record GlossaryHit(string Source, string Target, string Category);

public sealed record FrameDetectionResult(
    IReadOnlyList<ParsedChatLine> VisibleLines,
    IReadOnlyList<ParsedChatLine> CandidateLines,
    IReadOnlyList<FrameDetectionDecision> Decisions,
    IReadOnlyList<ParsedChatLine> NewLines,
    bool HasVisibleChat,
    bool ChatCycleJustReset);

public sealed record FrameDetectionDecision(
    ParsedChatLine Line,
    bool Accepted,
    string Reason,
    string Key);
