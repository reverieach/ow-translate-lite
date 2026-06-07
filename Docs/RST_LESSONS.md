# RST Implementation Lessons Used

The new app is intentionally not a fork of RSTGameTranslation. These are the implementation lessons retained after reading the project:

## Useful Patterns

- Keep capture, OCR, translation, and overlay as separate stages with a shared text-line model.
- Use a dedicated full-screen selector window and convert WPF points with `PointToScreen` to avoid high-DPI drift.
- Use a topmost transparent WPF overlay for translated text, with Win32 extended styles for click-through mode.
- OCR should return line bounds, not just plain text, because later UI placement and chat-line merging need geometry.
- Deduplicate repeated OCR frames before requesting network translation.
- Post-process model output with glossary terms because LLMs can still paraphrase hero or ability names.
- Treat OneOCR native files as runtime content and isolate P/Invoke behind one engine class.

## Things Removed

- Generic multi-translator UI.
- External Python OCR servers.
- Whisper, TTS, speech queues, clipboard translation, comic mode, large localization system.
- Complex text-object replacement for every OCR box. OW chat needs one compact translation overlay, not per-word painting.

## New OW-Specific Decisions

- Parse only `speaker: message` style chat lines.
- Skip Chinese UI/system hints such as hero-switch messages.
- Default target language is Simplified Chinese.
- DeepSeek and OpenAI-compatible providers share one JSON prompt contract.
- Glossary is UTF-8 JSON and should remain manually maintainable.
