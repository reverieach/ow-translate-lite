# ReplayLab

ReplayLab replays frame-sequence sessions recorded from the beta test panel.
It does not call OneOCR or any translation API. It reads recorded raw OCR lines,
then reruns:

```text
raw OCR lines -> OcrTextPostProcessor -> OwChatParser -> current new-message detection
```

## Run

```powershell
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- <session-directory>
```

With assertions:

```powershell
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- <session-directory> <expected.json>
```

The 3-account golden-case scripts use prewritten expectation templates:

```text
Tools\ReplayLab\expected-templates\<case-id>\expected.json
```

Smoke fixture:

```powershell
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- Tools\ReplayLab\fixtures\smoke-korean-short Tools\ReplayLab\fixtures\smoke-korean-short\expected.json
```

Korean similarity regression:

```powershell
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- --similarity Tools\ReplayLab\similarity\korean-jamo-regression.json
```

Timeline smoke regression:

```powershell
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- --timeline-smoke
```

ReplayLab writes `trace.json` and `report.md` under the session's
`replay-output/<timestamp>/` folder by default.

The markdown report includes:

- Accepted messages.
- Missing/duplicate/out-of-order/extra metrics.
- Per-frame raw/parsed/new-message counts.
- Variant Summary: timeline keys whose OCR text differed across frames. Use this to find repeated Korean jamo or spacing jitter before tuning `Resources/KoreanJamoConfusionCosts.json`.

## Expected File

```json
{
  "caseId": "case01-korean-short-cold-start",
  "expectedMessages": [
    {
      "speaker": "PLAYER1",
      "sourceText": "가자"
    }
  ],
  "allowedMissingCount": 0,
  "allowedDuplicateCount": 0,
  "allowedOutOfOrderCount": 0,
  "allowedExtraCount": 0
}
```

For golden cases, keep thresholds at zero unless a case explicitly documents a
known acceptable system-message false positive.

## Regression Commands

Run these after every refactor commit that can affect detection, de-dupe,
sampling, queueing, or overlay order:

```powershell
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- --timeline-smoke
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- --similarity Tools\ReplayLab\similarity\korean-jamo-regression.json
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- Tools\ReplayLab\fixtures\smoke-korean-short Tools\ReplayLab\fixtures\smoke-korean-short\expected.json
E:\rstgametranslation\.dotnet\dotnet.exe build OwTranslateLite.csproj -c Release
```

Current smoke fixture acceptance target is strict zero:

```text
missing=0, duplicates=0, outOfOrder=0, extra=0
```
