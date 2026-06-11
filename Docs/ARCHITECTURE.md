# Architecture

## Runtime Flow

```text
selected region
  -> GDI screenshot
  -> OW color-preserving preprocessing
  -> OneOCR automatic recognition
  -> OW OCR post-processing
  -> OW chat parser
  -> duplicate suppression
  -> DeepSeek / OpenAI-compatible translation
  -> glossary post-processing
  -> overlay records
```

## Main Modules

- `Core/AppSettings.cs`: persisted user settings.
- `Core/ConfigStore.cs`: UTF-8 JSON settings in `%APPDATA%\OWTranslatorLite`.
- `Core/SecretStore.cs`: Windows DPAPI protection for local API keys.
- `Core/SettingsMigrator.cs`: legacy setting normalization and API key migration.
- `Core/OcrTextPostProcessor.cs`: player-boundary repair and wrapped-line merge before parsing.
- `Core/DiagnosticsService.cs`: beta diagnostics, runtime log, dedupe log, and redacted report export.
- `Core/OwGlossaryService.cs`: glossary load, OCR normalization, prompt context, term locking.
- `Core/OwChatParser.cs`: player-chat extraction and Chinese UI filtering.
- `Core/TranslationCoordinator.cs`: capture/OCR/parse/translate loop, duplicate suppression, and optional raw screenshot capture for OCR lab fixtures when genuinely new chat lines are detected.
- `Core/TranslationQueueStatusTracker.cs`: queue observability for diagnostics.
- `Ocr/OneOcrEngine.cs`: native OneOCR wrapper.
- `Ocr/OcrEngineManager.cs`: OneOCR instance reuse, serialization, and disposal boundary.
- `Ocr/OcrImagePreprocessor.cs`: single production preprocessing path with color-preserving 2x scale, light contrast/gamma enhancement, and light sharpen.
- `Translation/OpenAICompatibleTranslationProvider.cs`: DeepSeek and OpenAI-compatible API.
- `Overlay/OverlayWindow.xaml`: topmost translation overlay.
- `Overlay/OverlayController.cs`: overlay lifecycle and event boundary.
- `AreaSelectorWindow.xaml`: capture region selector.
- `Tools/OcrPreprocessLab`: local OCR preprocessing comparison tool for production `ColorPreserving`, grayscale baselines, no-sharpen variants, and parameter sweeps.
- `Tools/GlossaryValidator`: OW glossary maintenance checker.

## OCR Preprocessing Status

The production OCR path intentionally has no selectable mask modes. Local testing on a broader screenshot corpus showed cyan and multi-color mask variants add complexity without a clear overall quality win. Keep mask experiments inside `Tools/OcrPreprocessLab` unless new reports show a stable improvement across cyan, green, and orange OW chat samples.

## Next Iterations

- Grow the real OW screenshot corpus and keep comparing OCR changes through `Tools/OcrPreprocessLab`.
- Continue improving OCR chunk merge and ordered-anchor dedupe.
- Add WGC capture for cases where GDI cannot capture exclusive/fullscreen content.
- Consider migrating from DPAPI settings storage to Windows Credential Manager if the UX needs account-level secret management.
- Remove or hide beta-only test entries before a formal release.
