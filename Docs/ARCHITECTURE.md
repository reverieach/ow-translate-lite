# Architecture

## Runtime Flow

```text
selected region
  -> GDI screenshot
  -> OneOCR
  -> OW OCR cleanup
  -> OW chat parser
  -> duplicate suppression
  -> DeepSeek / OpenAI-compatible translation
  -> glossary post-processing
  -> overlay records
```

## Main Modules

- `Core/AppSettings.cs`: persisted user settings.
- `Core/ConfigStore.cs`: UTF-8 JSON settings in `%APPDATA%\OWTranslatorLite`.
- `Core/OwGlossaryService.cs`: glossary load, OCR normalization, prompt context, term locking.
- `Core/OwChatParser.cs`: player-chat extraction and Chinese UI filtering.
- `Core/TranslationCoordinator.cs`: capture/OCR/parse/translate loop and duplicate suppression.
- `Ocr/OneOcrEngine.cs`: native OneOCR wrapper.
- `Translation/OpenAICompatibleTranslationProvider.cs`: DeepSeek and OpenAI-compatible API.
- `Overlay/OverlayWindow.xaml`: topmost translation overlay.
- `AreaSelectorWindow.xaml`: capture region selector.

## Next Iterations

- Add screenshot fixture tests for real OW chat images.
- Continue improving OCR chunk merge and ordered-anchor dedupe.
- Add image preprocessing presets for EN/JA/KO chat if screenshot fixtures show clear gains.
- Add WGC capture for cases where GDI cannot capture exclusive/fullscreen content.
- Encrypt the local API key with DPAPI or the Windows credential store.
- Remove or hide beta-only test entries before a formal release.
