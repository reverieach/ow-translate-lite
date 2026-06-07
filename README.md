# OW Translator Lite

Lightweight Overwatch 2 real-time OCR translation overlay for Chinese players.

## Scope

- Screen-region OCR for OW chat/subtitle areas.
- OCR engine: OneOCR.
- Translation providers: DeepSeek and OpenAI-compatible chat completions API.
- `Local Rules` is kept only as a beta/offline smoke-test mode, not as the product translation path.
- OW-specific filtering: translates player chat lines and ignores Chinese system/UI hints.
- OW glossary: heroes, abilities, maps, modes, common English/Japanese/Korean aliases, and Chinese community slang.
- Transparent topmost overlay with optional click-through.

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
2. For translation-quality tests, use `DeepSeek`, set API URL to `https://api.deepseek.com`, click `获取模型`, and select `deepseek-v4-flash`.
3. For no-network smoke tests only, use provider `Local Rules`.
4. Open Notepad and type OW chat-like lines:

```text
[TEAM] PlayerOne: group up
[TEAM] kiriMain: suzu no
[MATCH] genji99: nano blade soon
```

5. Select the Notepad text region.
6. Click Start.
7. Confirm that only player messages appear in the overlay.

## Reply Helper

- Press `Ctrl+Shift+Enter` to open the overlay reply input.
- Type Chinese and press Enter; the app translates it to the selected target language and copies the result.
- The target language dropdown supports Auto, English, Japanese, and Korean. Auto uses recent OCR chat language and falls back to English.
- Press Esc in the reply input to leave reply mode and restore click-through behavior.

## Git

This folder is its own local Git repository. Keep the previous fork-based experiment in `E:\rstgametranslation\ow-rst` separate.
