using System.Windows;

namespace OwTranslateLite.Core;

public sealed record OcrTextLine(string Text, Rect Bounds);

public sealed record ParsedChatLine(string Speaker, string SourceText, Rect Bounds, IReadOnlyList<GlossaryHit> GlossaryHits);

public sealed record TranslationRecord(string Speaker, string SourceText, string TranslatedText, DateTime Timestamp);

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
