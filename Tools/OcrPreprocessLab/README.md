# OCR Preprocess Lab

This local console tool compares OW chat OCR preprocessing variants against screenshot fixtures. The production app currently uses `ColorPreserving`: color-preserving 2x scale, light contrast/gamma enhancement, and light sharpen. Mask variants were removed from the production path after broader local testing, so keep new mask ideas inside this lab until they beat the baseline across real cyan, green, and orange OW chat samples.

```powershell
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\OcrPreprocessLab\OcrPreprocessLab.csproj -c Release
```

By default it reads `ow-screenshot\`, also merges `captured-screenshots\` when that directory exists, and writes previews plus `report.md` into `Docs\ocr-lab-output\<timestamp>\`.

Optional arguments:

```powershell
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\OcrPreprocessLab\OcrPreprocessLab.csproj -c Release -- --mode basic
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\OcrPreprocessLab\OcrPreprocessLab.csproj -c Release -- --mode all
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\OcrPreprocessLab\OcrPreprocessLab.csproj -c Release -- --mode sweep
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\OcrPreprocessLab\OcrPreprocessLab.csproj -c Release -- --mode gate
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\OcrPreprocessLab\OcrPreprocessLab.csproj -c Release -- --mode record --label no-text --duration 60 --interval 1000
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\OcrPreprocessLab\OcrPreprocessLab.csproj -c Release -- --input E:\path\to\screenshots --output E:\path\to\report
```

Modes:

- `basic`: production `ColorPreserving` only.
- `all`: production pipeline plus grayscale baselines and no-sharpen comparison.
- `sweep`: production pipeline plus contrast/gamma/scale parameter sweeps.
- `gate`: skip OCR and compare gate algorithms, writing `gate-report.md` with diff, trigger decisions, scores, timing, and label-aware false positive/false negative summaries when labels exist.
- `record`: capture the currently configured chat ROI from `%AppData%\OWTranslatorLite\settings.json` into a bounded gate case with `gate-case.json` metadata.

## Gate Recording Workflow

Use the helper script from the repository root after selecting the OW chat region in the main app:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\RecordOcrGateCase.ps1 -Label no-text -DurationSeconds 60 -IntervalMs 1000
powershell -ExecutionPolicy Bypass -File Tools\RecordOcrGateCase.ps1 -Label text -DurationSeconds 60 -IntervalMs 1000
```

Recommended labels:

- `no-text`: OW is running and the selected chat ROI has no player chat text.
- `text`: actual player chat/history text is visible in the selected ROI.
- `unknown`: exploratory capture that should not count toward accuracy.

The recorder saves each frame directly to disk, not memory. It clamps duration to 180 seconds and max frames to 360, so it cannot accidentally capture thousands of images. Defaults are 60 seconds, 1000 ms interval, and 360 max frames. Pass `-Region "left,top,width,height"` only when you want to override the app's saved chat region.

Evaluate one or more recorded case directories:

```powershell
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\OcrPreprocessLab\OcrPreprocessLab.csproj -c Release -- --mode gate --input Docs\ocr-lab-output\gate-recordings
```

The report compares:

- Baseline single-frame gate: the current high-contrast text-presence heuristic.
- Stable multi-frame gate: a C# lab-only gate that looks for stable, text-like horizontal rows across consecutive frames.

For labeled recordings, use `No-text rejection rate` and `Text recall` as the main decision metrics. A gate is not a candidate for the main app unless it rejects many `no-text` frames while keeping `text` recall high.

Auxiliary color sampling scripts live in `Tools\sample_colors.py` and `Tools\sample_colors_enhanced.py`. Run them from the repository root after collecting `captured-screenshots\`; they are exploratory and may install/use Python packages locally.
