using OwTranslateLite.Core;

namespace OwTranslateLite.Translation;

public sealed class LocalTranslationProvider : ITranslationProvider
{
    private readonly OwGlossaryService _glossary;

    public LocalTranslationProvider(OwGlossaryService glossary)
    {
        _glossary = glossary;
    }

    public string Name => "Local Rules";

    public Task<IReadOnlyList<TranslationResult>> TranslateAsync(IReadOnlyList<ParsedChatLine> lines, CancellationToken cancellationToken)
    {
        IReadOnlyList<TranslationResult> results = lines
            .Select(line =>
            {
                string translated = _glossary.TryLocalTranslate(line.SourceText);
                return new TranslationResult(line, _glossary.ApplyTerms(translated));
            })
            .ToList();
        return Task.FromResult(results);
    }
}
