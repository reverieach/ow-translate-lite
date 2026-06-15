# Architecture

## Runtime Flow

```text
selected region
  -> GDI screenshot patrol
  -> pixel diff gate
  -> burst OCR when chat region changes
  -> OW color-preserving preprocessing
  -> OneOCR automatic recognition
  -> OW OCR post-processing
  -> OW chat parser
  -> ChatTimeline suffix alignment
  -> multi-frame consensus
  -> translation queue
  -> DeepSeek / OpenAI-compatible translation
  -> glossary post-processing
  -> Seq-sorted overlay records
```

## Main Modules

- `Core/AppSettings.cs`: persisted user settings.
- `Core/ConfigStore.cs`: UTF-8 JSON settings in `%APPDATA%\OWTranslatorLite`.
- `Core/SecretStore.cs`: Windows DPAPI protection for local API keys.
- `Core/SettingsMigrator.cs`: legacy setting normalization and API key migration.
- `Core/OcrTextPostProcessor.cs`: player-boundary repair and geometry-first wrapped-line merge before parsing, including Korean continuation lines with incidental CJK OCR noise.
- `Core/DiagnosticsService.cs`: beta diagnostics, runtime/crash/debug logs, and redacted feedback package export.
- `Core/FrameSequenceRecorder.cs`: beta frame-sequence recording for ReplayLab fixtures.
- `Core/FrameDiffGate.cs`: low-cost pixel signature gate used by the main loop to patrol the selected chat region without running OCR on stable frames.
- `Core/ChatTimeline.cs`: authoritative ordered chat log. Message identity is the aligned timeline `Seq`, not content equality.
- `Core/TimelineAlignmentDetector.cs`: suffix alignment between visible OW chat lines and the authoritative timeline, including cold start and after-empty safeguards.
- `Core/KoreanJamoNormalizer.cs`: Hangul NFD/jamo normalization, whitespace-insensitive comparison, weighted jamo similarity, and public-data seeded confusion costs.
- `Core/OwGlossaryService.cs`: glossary load, OCR normalization, prompt context, term locking.
- `Core/OwChatParser.cs`: player-chat extraction and dominant-script filtering. Any Hangul/Kana player message is kept; Chinese-dominant lines without Hangul are treated as OW/system UI noise.
- `Core/TranslationCoordinator.cs`: capture/OCR/parse/translate coordination, Timeline alignment, multi-frame confirmation, explicit translation retry, and optional raw screenshot capture for OCR lab fixtures when genuinely new chat lines are detected.
- `Core/TranslationQueueStatusTracker.cs`: queue observability for diagnostics.
- `Ocr/OneOcrEngine.cs`: native OneOCR wrapper.
- `Ocr/OcrEngineManager.cs`: OneOCR instance reuse, serialization, and disposal boundary.
- `Ocr/OcrImagePreprocessor.cs`: single production preprocessing path with color-preserving 2x scale, light contrast/gamma enhancement, and light sharpen.
- `Translation/OpenAICompatibleTranslationProvider.cs`: DeepSeek and OpenAI-compatible API.
- `Overlay/OverlayWindow.xaml`: topmost translation overlay.
- `Overlay/OverlayController.cs`: overlay lifecycle and event boundary.
- `AreaSelectorWindow.xaml`: capture region selector.
- `Tools/OcrPreprocessLab`: local OCR preprocessing comparison tool for production `ColorPreserving`, grayscale baselines, no-sharpen variants, and parameter sweeps.
- `Tools/ReplayLab`: offline replay of recorded frame sequences with missing/duplicate/order assertions.
- `Tools/GlossaryValidator`: OW glossary maintenance checker.

## OCR Preprocessing Status

The production OCR path intentionally has no selectable mask modes. Local testing on a broader screenshot corpus showed cyan and multi-color mask variants add complexity without a clear overall quality win. Keep mask experiments inside `Tools/OcrPreprocessLab` unless new reports show a stable improvement across cyan, green, and orange OW chat samples.

## Message Identity And De-Dupe

Message identity is determined only by alignment to `ChatTimeline.Seq`. Content similarity is a scoring function for suffix alignment, not a standalone de-dupe rule. The old per-cycle seen set, recent TTL cache, pending fuzzy match, display-layer duplicate filter, and anchor/tail truncation path have been removed.

The visible OW chat is treated as an append-only suffix of the authoritative timeline. New timeline messages enter `Confirming`; they are queued for translation after two consistent observations, or after at most two confirmation observations when OCR variants disagree. For Korean, whitespace is ignored and jamo distance/similarity are used to absorb light OneOCR jitter. The text sent to translation is the timeline consensus text.

## Sampling And Translation Queue

The main loop has two sampling modes:

- Patrol: roughly 250-300 ms screenshots plus `FrameDiffGate` pixel signatures, with no OCR on stable frames.
- Burst: when the selected chat region changes, run 3 OCR frames at the same cadence, then fall back to patrol.

The translation side keeps a single worker. Normal batches are small; when backlog reaches the soft threshold, the worker takes a larger batch instead of dropping old messages. A hard safety limit remains to avoid unbounded memory growth, and any skip is logged explicitly.

## Overlay Visibility

Overlay records carry timeline `Seq` and are sorted by `Seq` before display. Translation failures retry explicitly up to two times and keep the original `Seq`.

Overlay visibility is level-triggered by OCR chat visibility: a visible chat frame shows the overlay and resets the no-chat counter; two consecutive no-chat OCR frames allow the normal idle-hide delay to hide it. Opening chat history therefore follows the same Timeline path as live chat and no longer uses a separate history-peek edge case.

## Korean OCR Robustness

Korean handling is deliberately layered:

- Parser: Hangul presence keeps a player message even if OneOCR injects a few CJK glyphs.
- Post-processing: wrapped continuation lines merge by geometry and script dominance, not Western line-ending conventions.
- Similarity: Hangul is normalized to jamo, spaces are ignored, short texts are scored, and a data-backed visual confusion cost table lowers substitution cost for plausible jamo OCR mistakes.

The seed table lives at `Resources/KoreanJamoConfusionCosts.json` and is copied into the app output. `KoreanJamoNormalizer` also has the same fallback table compiled in, so the app still runs if the file is missing.

## Next Iterations

- Grow the real OW screenshot corpus and keep comparing OCR changes through `Tools/OcrPreprocessLab`.
- Grow ReplayLab golden cases and use the variant summary to calibrate the jamo confusion table with real OW samples.
- Add WGC capture for cases where GDI cannot capture exclusive/fullscreen content.
- Consider migrating from DPAPI settings storage to Windows Credential Manager if the UX needs account-level secret management.
- Remove or hide beta-only test entries before a formal release.
