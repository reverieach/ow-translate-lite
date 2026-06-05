# OW Translator Lite

Lightweight Overwatch 2 real-time OCR translation overlay for Chinese players.

## Scope

- Screen-region OCR for OW chat/subtitle areas.
- OCR engines: OneOCR and Windows OCR.
- Translation providers: DeepSeek, OpenAI-compatible chat completions API, plus `Local Rules` for offline rule smoke tests.
- OW-specific filtering: translates player chat lines and ignores Chinese system/UI hints.
- OW glossary: heroes, abilities, common English/Japanese/Korean/Russian aliases, Chinese community slang.
- Transparent topmost overlay with optional click-through.

## Build

Use the local SDK installed at `E:\rstgametranslation\.dotnet`:

```powershell
& "E:\rstgametranslation\.dotnet\dotnet.exe" build OwTranslateLite.csproj
& "E:\rstgametranslation\.dotnet\dotnet.exe" publish OwTranslateLite.csproj -c Release
```

The release executable is expected under:

```text
E:\rstgametranslation\ow-translate-lite\app\win-x64\publish\OWTranslatorLite.exe
```

## First Test

1. Run the executable.
2. For translation-quality tests, use `DeepSeek`, set API URL to `https://api.deepseek.com`, click `获取模型`, and select `deepseek-v4-flash` or `deepseek-v4-pro`.
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

## Git

This folder is its own local Git repository. Keep the previous fork-based experiment in `E:\rstgametranslation\ow-rst` separate.
