# OCR Preprocess Lab

This local console tool compares OW chat OCR preprocessing modes against screenshot fixtures.

```powershell
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\OcrPreprocessLab\OcrPreprocessLab.csproj -c Release
```

By default it reads `ow-screenshot\` and writes previews plus `report.md` into `Docs\ocr-lab-output\<timestamp>\`.

Optional arguments:

```powershell
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\OcrPreprocessLab\OcrPreprocessLab.csproj -c Release -- <input-image-folder> <output-folder>
```
