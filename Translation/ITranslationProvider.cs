using OwTranslateLite.Core;

namespace OwTranslateLite.Translation;

public interface ITranslationProvider
{
    string Name { get; }
    Task<IReadOnlyList<TranslationResult>> TranslateAsync(IReadOnlyList<ParsedChatLine> lines, CancellationToken cancellationToken);
}
