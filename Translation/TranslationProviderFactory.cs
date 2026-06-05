using OwTranslateLite.Core;

namespace OwTranslateLite.Translation;

public static class TranslationProviderFactory
{
    public static ITranslationProvider Create(AppSettings settings, OwGlossaryService glossary)
    {
        return settings.TranslationProvider switch
        {
            "Local" => new LocalTranslationProvider(glossary),
            "Local Rules" => new LocalTranslationProvider(glossary),
            "DeepSeek" => new OpenAICompatibleTranslationProvider(settings, glossary),
            "OpenAI Compatible" => new OpenAICompatibleTranslationProvider(settings, glossary),
            _ => new OpenAICompatibleTranslationProvider(settings, glossary)
        };
    }
}
