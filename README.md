# OW Translator Lite

Lightweight Overwatch 2 real-time OCR translation overlay for Chinese players.

## Scope

- Screen-region OCR for OW chat/subtitle areas.
- OCR engine: local OneOCR automatic recognition.
- Translation providers: DeepSeek and OpenAI-compatible chat completions API.
- OW-specific filtering: translates player chat lines and ignores Chinese system/UI hints.
- OW glossary: heroes, abilities, maps, modes, common English/Japanese/Korean aliases, and Chinese community slang.
- Transparent topmost overlay with optional click-through.
- Reply helper: optional overlay input bar translates Chinese replies to English/Japanese/Korean and copies to clipboard.
- User API Key is stored through Windows DPAPI as `apiKeyProtected`.
- Optional beta diagnostics include dedupe logging and local raw screenshot capture for OCR preprocessing experiments.

## Build

Use the local SDK installed at `E:\rstgametranslation\.dotnet`:

```powershell
& "E:\rstgametranslation\.dotnet\dotnet.exe" build OwTranslateLite.csproj -c Release
```

Publish beta packages only when preparing a tester build:

```powershell
& "E:\rstgametranslation\.dotnet\dotnet.exe" publish OwTranslateLite.csproj -c Release -o E:\rstgametranslation\ow-translate-lite\dist\OWTranslatorLite-vX.Y.Z-portable-win-x64
```

## First Test

1. Run the executable.
2. Use `DeepSeek`, set API URL to `https://api.deepseek.com`, click `获取模型`, and select `deepseek-v4-flash`.
3. Open Notepad and type OW chat-like lines:

```text
[TEAM] PlayerOne: group up
[TEAM] kiriMain: suzu no
[MATCH] genji99: nano blade soon
```

4. Select the Notepad text region.
5. Click Start.
6. Confirm that only player messages appear in the overlay.

## Reply Helper

- Click the reply input at the bottom of the overlay.
- Type Chinese and press Enter; the app translates it to the selected target language and copies the result.
- The target language dropdown supports Auto, English, Japanese, and Korean. Auto uses recent OCR chat language and falls back to English.
- After Enter, the input releases focus and restores click-through behavior.
- Optional reply hotkeys can be enabled in the main window; they are off by default.
- The input bar can be hidden from the main window; hotkey mode can still show it temporarily.

## Local Tools

```powershell
& "E:\rstgametranslation\.dotnet\dotnet.exe" run --project Tools\OcrPreprocessLab\OcrPreprocessLab.csproj -c Release
& "E:\rstgametranslation\.dotnet\dotnet.exe" run --project Tools\OcrPreprocessLab\OcrPreprocessLab.csproj -c Release -- --mode sweep
& "E:\rstgametranslation\.dotnet\dotnet.exe" run --project Tools\GlossaryValidator\GlossaryValidator.csproj -c Release
```

`OcrPreprocessLab` compares the production `ColorPreserving` pipeline against lab-only grayscale/no-sharpen/sweep variants. By default it reads `ow-screenshot\` and also `captured-screenshots\` when present, then writes previews and `report.md` under `Docs\ocr-lab-output\`. `GlossaryValidator` checks the OW glossary for JSON errors, empty targets, duplicate aliases, and short alias risks.

## Git

This folder is its own local Git repository. Keep the previous fork-based experiment in `E:\rstgametranslation\ow-rst` separate.
