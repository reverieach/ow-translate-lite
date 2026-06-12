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

Smoke fixture:

```powershell
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- Tools\ReplayLab\fixtures\smoke-korean-short Tools\ReplayLab\fixtures\smoke-korean-short\expected.json
```

ReplayLab writes `trace.json` and `report.md` under the session's
`replay-output/<timestamp>/` folder by default.

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
