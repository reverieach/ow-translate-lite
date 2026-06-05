using System.Windows;

namespace OwTranslateLite.Core;

public sealed record OcrTextLine(string Text, Rect Bounds);

public sealed record ParsedChatLine(string Speaker, string SourceText, Rect Bounds, IReadOnlyList<GlossaryHit> GlossaryHits);

public sealed record TranslationRecord(string Speaker, string SourceText, string TranslatedText, DateTime Timestamp);

public sealed record TranslationResult(ParsedChatLine SourceLine, string TranslatedText);

public sealed record GlossaryHit(string Source, string Target, string Category);
