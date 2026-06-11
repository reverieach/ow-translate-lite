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
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\OcrPreprocessLab\OcrPreprocessLab.csproj -c Release -- --input E:\path\to\screenshots --output E:\path\to\report
```

Modes:

- `basic`: production `ColorPreserving` only.
- `all`: production pipeline plus grayscale baselines and no-sharpen comparison.
- `sweep`: production pipeline plus contrast/gamma/scale parameter sweeps.

Auxiliary color sampling scripts live in `Tools\sample_colors.py` and `Tools\sample_colors_enhanced.py`. Run them from the repository root after collecting `captured-screenshots\`; they are exploratory and may install/use Python packages locally.
