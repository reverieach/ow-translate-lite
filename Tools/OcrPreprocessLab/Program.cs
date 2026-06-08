using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using OwTranslateLite.Core;
using OwTranslateLite.Ocr;

Console.OutputEncoding = Encoding.UTF8;

string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
string inputDir = "";
string outputDir = "";
string runMode = "all"; // "basic", "all", "sweep"

// Parse CLI args
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--input":
            inputDir = Path.GetFullPath(args[++i]);
            break;
        case "--output":
            outputDir = Path.GetFullPath(args[++i]);
            break;
        case "--mode":
            runMode = args[++i];
            break;
        default:
            if (i == 0 && !args[i].StartsWith("--"))
            {
                inputDir = Path.GetFullPath(args[i]);
            }
            else if (i == 1 && !args[i].StartsWith("--"))
            {
                outputDir = Path.GetFullPath(args[i]);
            }

            break;
    }
}

if (string.IsNullOrEmpty(inputDir))
{
    inputDir = Path.Combine(repoRoot, "ow-screenshot");
}

if (string.IsNullOrEmpty(outputDir))
{
    outputDir = Path.Combine(repoRoot, "Docs", "ocr-lab-output", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
}

Directory.CreateDirectory(outputDir);
string previewDir = Path.Combine(outputDir, "previews");
Directory.CreateDirectory(previewDir);

// Collect images from input dir AND captured-screenshots if they exist
List<string> imagePaths = [];
CollectImagesFromDir(inputDir, imagePaths);
string capturedDir = Path.Combine(repoRoot, "captured-screenshots");
if (Directory.Exists(capturedDir))
{
    CollectImagesFromDir(capturedDir, imagePaths);
}

if (imagePaths.Count == 0)
{
    Console.Error.WriteLine($"No png/jpg/bmp images found in: {inputDir}");
    if (Directory.Exists(capturedDir))
    {
        Console.Error.WriteLine($"  or in: {capturedDir}");
    }

    Environment.ExitCode = 2;
    return;
}

// Build variant list
List<PreprocessVariant> variants = runMode switch
{
    "basic" => BuildBasicVariants(),
    "sweep" => BuildSweepVariants(),
    _ => BuildAllVariants()
};

Console.WriteLine($"Mode: {runMode}");
Console.WriteLine($"Images: {imagePaths.Count}");
Console.WriteLine($"Variants: {variants.Count}");
Console.WriteLine($"Output: {outputDir}");
Console.WriteLine();

List<LabResult> results = [];
OwChatParser parser = new(OwGlossaryService.LoadDefault());
using OneOcrEngine engine = new();

foreach (string imagePath in imagePaths)
{
    using Bitmap source = new(imagePath);
    foreach (PreprocessVariant variant in variants)
    {
        string imageName = Path.GetFileNameWithoutExtension(imagePath);
        string safeName = MakeSafeFileName(imageName);
        string previewPath = Path.Combine(previewDir, $"{safeName}.{variant.Name}.png");
        try
        {
            using Bitmap preview = variant.Prepare(source);
            preview.Save(previewPath, ImageFormat.Png);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{Path.GetFileName(imagePath)} | {variant.Name} | PREPARE ERROR: {ex.Message}");
            continue;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyList<OcrTextLine> lines;
        try
        {
            lines = await engine.RecognizeAsync(source, "auto", OcrPreprocessingMode.ColorPreserving, CancellationToken.None, variant.Prepare);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{Path.GetFileName(imagePath)} | {variant.Name} | OCR ERROR: {ex.Message}");
            continue;
        }

        stopwatch.Stop();

        IReadOnlyList<OcrTextLine> processedLines = OcrTextPostProcessor.Process(lines);
        IReadOnlyList<ParsedChatLine> parsedLines = parser.Parse(processedLines);
        IReadOnlyList<string> effectiveLines = lines
            .Select(line => line.Text.Trim())
            .Where(IsEffectiveLine)
            .ToArray();
        IReadOnlyList<string> rawLines = lines
            .Select(line => line.Text.Trim())
            .Where(static text => text.Length > 0)
            .ToArray();

        bool hasNoise = rawLines.Any(static line => line.Contains('串') || line.Contains('◆'));

        results.Add(new LabResult(
            imagePath,
            variant.Name,
            stopwatch.Elapsed,
            rawLines,
            processedLines.Select(line => line.Text.Trim()).Where(static text => text.Length > 0).ToArray(),
            parsedLines.Select(static line => $"[{line.Speaker}]: {line.SourceText}").ToArray(),
            effectiveLines,
            previewPath,
            hasNoise));

        Console.WriteLine($"{Path.GetFileName(imagePath)} | {variant.Name} | {stopwatch.ElapsedMilliseconds} ms | lines={lines.Count} parsed={parsedLines.Count} effective={effectiveLines.Count}{(hasNoise ? " ⚠NOISE" : "")}");
    }
}

string reportPath = Path.Combine(outputDir, "report.md");
File.WriteAllText(reportPath, BuildEnhancedReport(inputDir, outputDir, capturedDir, results, runMode), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine();
Console.WriteLine($"Report: {reportPath}");
Console.WriteLine($"Suggested overall mode: {GetSuggestedMode(results)}");

// ============================================================
// Image collection
// ============================================================

static void CollectImagesFromDir(string dir, List<string> paths)
{
    if (!Directory.Exists(dir))
    {
        return;
    }

    foreach (string path in Directory.EnumerateFiles(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
    {
        if (IsSupportedImage(path) && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(path);
        }
    }
}

// ============================================================
// Variant builders
// ============================================================

static List<PreprocessVariant> BuildBasicVariants()
{
    return
    [
        new("ColorPreserving", OcrImagePreprocessor.PrepareColorPreserving),
        new("OwChatCyanMask", bitmap => OcrImagePreprocessor.Prepare(bitmap, OcrPreprocessingMode.OwChatCyanMask)),
        new("OwChatMultiColorMask", bitmap => OcrImagePreprocessor.Prepare(bitmap, OcrPreprocessingMode.OwChatMultiColorMask)),
        new("OwChatMultiColorMaskThickened", bitmap => OcrImagePreprocessor.Prepare(bitmap, OcrPreprocessingMode.OwChatMultiColorMaskThickened)),
    ];
}

static List<PreprocessVariant> BuildAllVariants()
{
    List<PreprocessVariant> list = BuildBasicVariants();
    list.AddRange(
    [
        new("GrayscaleBaseline", LabPreprocess.GrayscaleBaseline),
        new("GrayscaleUpscaled", LabPreprocess.GrayscaleUpscaled),
        new("GrayscaleOtsu", LabPreprocess.GrayscaleOtsu),
        new("ColorPreserving_NoSharpen", LabPreprocess.ColorPreserving_NoSharpen),
        new("MultiColorMask_NoSharpen", LabPreprocess.MultiColorMask_NoSharpen),
        new("CyanMask_NoSharpen", LabPreprocess.CyanMask_NoSharpen),
    ]);
    return list;
}

static List<PreprocessVariant> BuildSweepVariants()
{
    List<PreprocessVariant> list =
    [
        new("ColorPreserving", OcrImagePreprocessor.PrepareColorPreserving),
        new("OwChatMultiColorMask", bitmap => OcrImagePreprocessor.Prepare(bitmap, OcrPreprocessingMode.OwChatMultiColorMask)),
    ];

    // Contrast sweep: 1.0, 1.18, 1.4
    foreach (float contrast in new[] { 1.0f, 1.18f, 1.4f })
    {
        list.Add(new($"Sweep_Contrast_{contrast:0.##}_NoMask",
            bitmap => LabPreprocess.SweepScaleEnhance(bitmap, contrast, 0.96f, sharpen: true)));
    }

    // Gamma sweep: 0.8, 0.96, 1.0
    foreach (float gamma in new[] { 0.8f, 0.96f, 1.0f })
    {
        if (Math.Abs(gamma - 0.96f) < 0.01f)
        {
            continue; // Already tested as ColorPreserving
        }

        list.Add(new($"Sweep_Gamma_{gamma:0.##}_NoMask",
            bitmap => LabPreprocess.SweepScaleEnhance(bitmap, 1.18f, gamma, sharpen: true)));
    }

    // Scale factor sweep: 1.5x, 2.5x, 3x (comparing to default 2x)
    foreach (int scale in new[] { 3, 4, 5 }) // map to 1.5x, 2.5x, 3x via division
    {
        float factor = scale / 2.0f;
        if (Math.Abs(factor - 2.0f) < 0.01f)
        {
            continue;
        }

        list.Add(new($"Sweep_Scale_{factor:0.#}x_NoMask",
            bitmap => LabPreprocess.SweepScaleFactor(bitmap, factor)));
    }

    return list;
}

// ============================================================
// Report building
// ============================================================

static string BuildEnhancedReport(string inputDir, string outputDir, string? capturedDir, IReadOnlyList<LabResult> results, string runMode)
{
    StringBuilder builder = new();
    builder.AppendLine("# OW OCR Preprocess Lab (Enhanced)");
    builder.AppendLine();
    builder.AppendLine($"- Input: `{inputDir}`");
    if (capturedDir is not null && Directory.Exists(capturedDir))
    {
        builder.AppendLine($"- Captured screenshots: `{capturedDir}`");
    }

    builder.AppendLine($"- Generated: `{DateTime.Now:yyyy-MM-dd HH:mm:ss}`");
    builder.AppendLine($"- Run mode: `{runMode}`");
    builder.AppendLine($"- Images: {results.Select(static r => r.ImagePath).Distinct().Count()}");
    builder.AppendLine($"- Variants: {results.Select(static r => r.Mode).Distinct().Count()}");
    builder.AppendLine($"- Suggested overall mode: `{GetSuggestedMode(results)}`");
    builder.AppendLine();

    // Ranking table — all results sorted by score
    builder.AppendLine("## Overall Ranking");
    builder.AppendLine();
    builder.AppendLine("| Rank | Mode | Avg Score | Avg Time | Avg Parsed | Avg Effective | Noise |");
    builder.AppendLine("| ---: | --- | ---: | ---: | ---: | ---: | ---: |");

    int rank = 0;
    foreach (IGrouping<string, LabResult> modeGroup in results
                 .GroupBy(static r => r.Mode)
                 .OrderByDescending(static g => g.Average(GetScore)))
    {
        rank++;
        int noiseCount = modeGroup.Count(static r => r.HasNoise);
        builder.AppendLine($"| {rank} | `{modeGroup.Key}` | {modeGroup.Average(GetScore):0.0} | {modeGroup.Average(static r => r.Elapsed.TotalMilliseconds):0} ms | {modeGroup.Average(static r => r.ParsedChatLines.Count):0.0} | {modeGroup.Average(static r => r.EffectiveLines.Count):0.0} | {(noiseCount > 0 ? $"⚠ {noiseCount}/{modeGroup.Count()}" : "✓")} |");
    }

    builder.AppendLine();

    // Full detail table
    builder.AppendLine("## Full Results");
    builder.AppendLine();
    builder.AppendLine("| Image | Mode | Time | OCR Lines | Parsed Chat | Effective | Noise | Score | Preview |");
    builder.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |");

    foreach (LabResult result in results.OrderBy(static r => Path.GetFileName(r.ImagePath), StringComparer.OrdinalIgnoreCase).ThenByDescending(GetScore))
    {
        string preview = Path.GetRelativePath(outputDir, result.PreviewPath).Replace('\\', '/');
        builder.AppendLine($"| {EscapePipe(Path.GetFileName(result.ImagePath))} | `{result.Mode}` | {result.Elapsed.TotalMilliseconds:0} ms | {result.RawLines.Count} | {result.ParsedChatLines.Count} | {result.EffectiveLines.Count} | {(result.HasNoise ? "⚠" : "")} | {GetScore(result):0.0} | [{Path.GetFileName(result.PreviewPath)}]({preview}) |");
    }

    builder.AppendLine();

    // Per-image breakdown
    foreach (IGrouping<string, LabResult> imageGroup in results.GroupBy(static result => result.ImagePath))
    {
        builder.AppendLine();
        builder.AppendLine($"## {Path.GetFileName(imageGroup.Key)}");
        builder.AppendLine();
        string bestMode = GetSuggestedMode(imageGroup);
        builder.AppendLine($"**Best mode: `{bestMode}`**");
        builder.AppendLine();

        foreach (LabResult result in imageGroup.OrderByDescending(GetScore))
        {
            builder.AppendLine($"### {result.Mode}");
            builder.AppendLine();
            builder.AppendLine($"- Time: `{result.Elapsed.TotalMilliseconds:0} ms`");
            builder.AppendLine($"- OCR lines: `{result.RawLines.Count}`");
            builder.AppendLine($"- Processed lines: `{result.ProcessedLines.Count}`");
            builder.AppendLine($"- Parsed chat lines: `{result.ParsedChatLines.Count}`");
            builder.AppendLine($"- Effective lines: `{result.EffectiveLines.Count}`");
            builder.AppendLine($"- Noise: {(result.HasNoise ? "⚠ yes" : "no")}");

            if (result.ParsedChatLines.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Parsed chat:");
                builder.AppendLine();
                builder.AppendLine("```text");
                foreach (string line in result.ParsedChatLines)
                {
                    builder.AppendLine(line);
                }

                builder.AppendLine("```");
            }

            builder.AppendLine();
            builder.AppendLine("Processed OCR:");
            builder.AppendLine();
            builder.AppendLine("```text");
            foreach (string line in result.ProcessedLines)
            {
                builder.AppendLine(line);
            }

            builder.AppendLine("```");
            builder.AppendLine();
            builder.AppendLine("Raw OCR:");
            builder.AppendLine();
            builder.AppendLine("```text");
            foreach (string line in result.RawLines)
            {
                builder.AppendLine(line);
            }

            builder.AppendLine("```");
        }
    }

    return builder.ToString();
}

// ============================================================
// Scoring and helpers
// ============================================================

static string GetSuggestedMode(IEnumerable<LabResult> results)
{
    return results
        .GroupBy(static result => result.Mode)
        .Select(static group => new
        {
            Mode = group.Key,
            AverageScore = group.Average(GetScore),
            AverageEffectiveLines = group.Average(static result => result.EffectiveLines.Count)
        })
        .OrderByDescending(static item => item.AverageScore)
        .ThenByDescending(static item => item.AverageEffectiveLines)
        .First().Mode;
}

static double GetScore(LabResult result)
{
    int effectiveChars = result.EffectiveLines.Sum(static line => line.Length);
    int rawChars = result.RawLines.Sum(static line => line.Length);
    int noiseLines = Math.Max(0, result.RawLines.Count - result.EffectiveLines.Count);
    return result.ParsedChatLines.Count * 220 +
           result.EffectiveLines.Count * 36 +
           effectiveChars * 1.4 +
           rawChars * 0.08 -
           noiseLines * 36 -
           result.Elapsed.TotalMilliseconds * 0.02;
}

static string FindRepoRoot(string startDirectory)
{
    string? current = startDirectory;
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current, "OwTranslateLite.csproj")))
        {
            return current;
        }

        current = Path.GetDirectoryName(current);
    }

    return Directory.GetCurrentDirectory();
}

static bool IsSupportedImage(string path)
{
    string extension = Path.GetExtension(path).ToLowerInvariant();
    return extension is ".png" or ".jpg" or ".jpeg" or ".bmp";
}

static bool IsEffectiveLine(string text)
{
    if (text.Length < 2)
    {
        return false;
    }

    int contentChars = text.Count(static ch =>
        char.IsLetterOrDigit(ch) ||
        IsHangul(ch) ||
        IsKana(ch) ||
        IsCjk(ch));
    return contentChars >= 2;
}

static string EscapePipe(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);

static string MakeSafeFileName(string value)
{
    StringBuilder builder = new(value.Length);
    foreach (char ch in value)
    {
        builder.Append(Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch);
    }

    return builder.ToString();
}

static bool IsHangul(char ch) => ch is >= '\uAC00' and <= '\uD7AF' or >= '\u1100' and <= '\u11FF';

static bool IsKana(char ch) => ch is >= '\u3040' and <= '\u30FF';

static bool IsCjk(char ch) => ch is >= '\u4E00' and <= '\u9FFF';

// ============================================================
// Data types
// ============================================================

internal sealed record PreprocessVariant(string Name, Func<Bitmap, Bitmap> Prepare);

internal sealed record LabResult(
    string ImagePath,
    string Mode,
    TimeSpan Elapsed,
    IReadOnlyList<string> RawLines,
    IReadOnlyList<string> ProcessedLines,
    IReadOnlyList<string> ParsedChatLines,
    IReadOnlyList<string> EffectiveLines,
    string PreviewPath,
    bool HasNoise = false);

// ============================================================
// Experimental preprocessing methods (lab-only, not in main preprocessor)
// ============================================================

internal static class LabPreprocess
{
    private const int ScaleFactor = 2;

    /// <summary>
    /// Pure grayscale — no scaling, no enhancement. The simplest baseline.
    /// </summary>
    public static Bitmap GrayscaleBaseline(Bitmap source)
    {
        Bitmap output = new(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(output);
        ColorMatrix grayMatrix = new(
        [
            [0.299f, 0.299f, 0.299f, 0, 0],
            [0.587f, 0.587f, 0.587f, 0, 0],
            [0.114f, 0.114f, 0.114f, 0, 0],
            [0, 0, 0, 1, 0],
            [0, 0, 0, 0, 1]
        ]);
        using ImageAttributes attributes = new();
        attributes.SetColorMatrix(grayMatrix);
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, output.Width, output.Height),
            0, 0, source.Width, source.Height,
            GraphicsUnit.Pixel,
            attributes);
        return output;
    }

    /// <summary>
    /// 2x upscale + grayscale, no color enhancement. Tests if scaling alone helps.
    /// </summary>
    public static Bitmap GrayscaleUpscaled(Bitmap source)
    {
        int width = Math.Max(1, source.Width * ScaleFactor);
        int height = Math.Max(1, source.Height * ScaleFactor);
        Bitmap scaled = new(width, height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(scaled);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);

        // Convert to grayscale
        Rectangle rect = new(0, 0, scaled.Width, scaled.Height);
        BitmapData data = scaled.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            byte[] buffer = new byte[stride * scaled.Height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            for (int y = 0; y < scaled.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < scaled.Width; x++)
                {
                    int index = row + x * 4;
                    byte gray = (byte)(buffer[index + 2] * 0.299f + buffer[index + 1] * 0.587f + buffer[index] * 0.114f);
                    buffer[index] = gray;
                    buffer[index + 1] = gray;
                    buffer[index + 2] = gray;
                }
            }

            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        }
        finally
        {
            scaled.UnlockBits(data);
        }

        return scaled;
    }

    /// <summary>
    /// 2x upscale + grayscale + Otsu binarization. Classic OCR preprocessing.
    /// </summary>
    public static Bitmap GrayscaleOtsu(Bitmap source)
    {
        using Bitmap grayscale = GrayscaleUpscaled(source);
        Rectangle rect = new(0, 0, grayscale.Width, grayscale.Height);
        BitmapData data = grayscale.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int[] histogram = new int[256];
        try
        {
            int stride = Math.Abs(data.Stride);
            byte[] buffer = new byte[stride * grayscale.Height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            for (int y = 0; y < grayscale.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < grayscale.Width; x++)
                {
                    histogram[buffer[row + x * 4]]++;
                }
            }
        }
        finally
        {
            grayscale.UnlockBits(data);
        }

        int threshold = ComputeOtsuThreshold(histogram, grayscale.Width * grayscale.Height);

        Bitmap binary = new(grayscale.Width, grayscale.Height, PixelFormat.Format32bppArgb);
        BitmapData binData = binary.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        BitmapData grayData = grayscale.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(grayData.Stride);
            byte[] srcBuffer = new byte[stride * grayscale.Height];
            byte[] dstBuffer = new byte[stride * grayscale.Height];
            Marshal.Copy(grayData.Scan0, srcBuffer, 0, srcBuffer.Length);
            for (int y = 0; y < grayscale.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < grayscale.Width; x++)
                {
                    int index = row + x * 4;
                    byte value = srcBuffer[index] >= threshold ? (byte)255 : (byte)0;
                    dstBuffer[index] = value;
                    dstBuffer[index + 1] = value;
                    dstBuffer[index + 2] = value;
                    dstBuffer[index + 3] = 255;
                }
            }

            Marshal.Copy(dstBuffer, 0, binData.Scan0, dstBuffer.Length);
        }
        finally
        {
            grayscale.UnlockBits(grayData);
            binary.UnlockBits(binData);
        }

        return binary;
    }

    /// <summary>
    /// Same as ColorPreserving but without the final light sharpen step.
    /// </summary>
    public static Bitmap ColorPreserving_NoSharpen(Bitmap source)
    {
        // Call internal ScaleColorPreserving via the public PrepareColorPreserving
        // minus the sharpen. We reimplement the scale step inline.
        int width = Math.Max(1, source.Width * ScaleFactor);
        int height = Math.Max(1, source.Height * ScaleFactor);
        Bitmap scaled = new(width, height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(scaled);
        using ImageAttributes attributes = CreateColorPreservingAttributes();
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, width, height),
            0, 0, source.Width, source.Height,
            GraphicsUnit.Pixel,
            attributes);
        return scaled;
    }

    /// <summary>
    /// MultiColorMask pipeline without the final sharpen step.
    /// </summary>
    public static Bitmap MultiColorMask_NoSharpen(Bitmap source)
    {
        using Bitmap scaled = ScaleColorPreservingInline(source);
        Bitmap masked = CreateOwChatMaskInline(scaled, includeGreenAndOrange: true);
        return masked;
    }

    /// <summary>
    /// CyanMask pipeline without the final sharpen step.
    /// </summary>
    public static Bitmap CyanMask_NoSharpen(Bitmap source)
    {
        using Bitmap scaled = ScaleColorPreservingInline(source);
        Bitmap masked = CreateOwChatMaskInline(scaled, includeGreenAndOrange: false);
        return masked;
    }

    /// <summary>
    /// Parameterized scale+enhance for contrast/gamma sweep.
    /// </summary>
    public static Bitmap SweepScaleEnhance(Bitmap source, float contrast, float gamma, bool sharpen)
    {
        int width = Math.Max(1, source.Width * ScaleFactor);
        int height = Math.Max(1, source.Height * ScaleFactor);
        Bitmap scaled = new(width, height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(scaled);
        float offset = 0.018f;
        float[][] matrix =
        [
            [contrast, 0, 0, 0, 0],
            [0, contrast, 0, 0, 0],
            [0, 0, contrast, 0, 0],
            [0, 0, 0, 1, 0],
            [offset, offset, offset, 0, 1]
        ];
        ImageAttributes attributes = new();
        attributes.SetColorMatrix(new ColorMatrix(matrix));
        attributes.SetGamma(gamma);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, width, height),
            0, 0, source.Width, source.Height,
            GraphicsUnit.Pixel,
            attributes);

        if (sharpen)
        {
            ApplyLightSharpenInline(scaled);
        }

        return scaled;
    }

    /// <summary>
    /// Parameterized scale factor sweep (without color enhancement).
    /// </summary>
    public static Bitmap SweepScaleFactor(Bitmap source, float factor)
    {
        int width = Math.Max(1, (int)(source.Width * factor));
        int height = Math.Max(1, (int)(source.Height * factor));
        Bitmap scaled = new(width, height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(scaled);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
        ApplyLightSharpenInline(scaled);
        return scaled;
    }

    // --- Inline copies of OcrImagePreprocessor internals (to avoid making them public) ---

    private static ImageAttributes CreateColorPreservingAttributes()
    {
        ImageAttributes attributes = new();
        const float contrast = 1.18f;
        const float offset = 0.018f;
        float[][] matrix =
        [
            [contrast, 0, 0, 0, 0],
            [0, contrast, 0, 0, 0],
            [0, 0, contrast, 0, 0],
            [0, 0, 0, 1, 0],
            [offset, offset, offset, 0, 1]
        ];
        attributes.SetColorMatrix(new ColorMatrix(matrix));
        attributes.SetGamma(0.96f);
        return attributes;
    }

    private static Bitmap ScaleColorPreservingInline(Bitmap source)
    {
        int width = Math.Max(1, source.Width * ScaleFactor);
        int height = Math.Max(1, source.Height * ScaleFactor);
        Bitmap scaled = new(width, height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(scaled);
        using ImageAttributes attributes = CreateColorPreservingAttributes();
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, width, height),
            0, 0, source.Width, source.Height,
            GraphicsUnit.Pixel,
            attributes);
        return scaled;
    }

    private static Bitmap CreateOwChatMaskInline(Bitmap source, bool includeGreenAndOrange)
    {
        Bitmap output = new(source.Width, source.Height, PixelFormat.Format32bppArgb);
        Rectangle rect = new(0, 0, source.Width, source.Height);
        BitmapData sourceData = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData outputData = output.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int sourceStride = Math.Abs(sourceData.Stride);
            int outputStride = Math.Abs(outputData.Stride);
            int sourceBytes = sourceStride * source.Height;
            int outputBytes = outputStride * output.Height;
            byte[] sourceBuffer = new byte[sourceBytes];
            byte[] outputBuffer = new byte[outputBytes];
            Marshal.Copy(sourceData.Scan0, sourceBuffer, 0, sourceBytes);

            for (int y = 0; y < source.Height; y++)
            {
                int sourceRow = y * sourceStride;
                int outputRow = y * outputStride;
                for (int x = 0; x < source.Width; x++)
                {
                    int sourceIndex = sourceRow + x * 4;
                    byte b = sourceBuffer[sourceIndex];
                    byte g = sourceBuffer[sourceIndex + 1];
                    byte r = sourceBuffer[sourceIndex + 2];
                    bool isText = IsOwChatCyanInline(r, g, b) ||
                                  (includeGreenAndOrange && (IsOwChatGreenInline(r, g, b) || IsOwChatOrangeInline(r, g, b)));

                    int outputIndex = outputRow + x * 4;
                    byte value = isText ? GetForegroundMaskValueInline(r, g, b) : (byte)0;
                    outputBuffer[outputIndex] = value;
                    outputBuffer[outputIndex + 1] = value;
                    outputBuffer[outputIndex + 2] = value;
                    outputBuffer[outputIndex + 3] = 255;
                }
            }

            Marshal.Copy(outputBuffer, 0, outputData.Scan0, outputBytes);
        }
        finally
        {
            source.UnlockBits(sourceData);
            output.UnlockBits(outputData);
        }

        return output;
    }

    private static void ApplyLightSharpenInline(Bitmap bitmap)
    {
        Rectangle rect = new(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            int bytes = stride * bitmap.Height;
            byte[] source = new byte[bytes];
            byte[] output = new byte[bytes];
            Marshal.Copy(data.Scan0, source, 0, bytes);
            Array.Copy(source, output, bytes);

            for (int y = 1; y < bitmap.Height - 1; y++)
            {
                for (int x = 1; x < bitmap.Width - 1; x++)
                {
                    int index = y * stride + x * 4;
                    for (int channel = 0; channel < 3; channel++)
                    {
                        int value =
                            source[index + channel] * 5 -
                            source[index - 4 + channel] -
                            source[index + 4 + channel] -
                            source[index - stride + channel] -
                            source[index + stride + channel];
                        output[index + channel] = ClampToByteInline(value);
                    }

                    output[index + 3] = source[index + 3];
                }
            }

            Marshal.Copy(output, 0, data.Scan0, bytes);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static byte GetForegroundMaskValueInline(byte r, byte g, byte b)
    {
        int value = Math.Max(Math.Max(r, g), b) + 35;
        return ClampToByteInline(value);
    }

    private static byte ClampToByteInline(int value)
    {
        if (value <= 0)
        {
            return 0;
        }

        return value >= 255 ? (byte)255 : (byte)value;
    }

    private static bool IsOwChatCyanInline(byte r, byte g, byte b)
    {
        return b >= 118 &&
               g >= 105 &&
               r <= 140 &&
               b >= r + 42 &&
               g >= r + 26;
    }

    private static bool IsOwChatGreenInline(byte r, byte g, byte b)
    {
        return g >= 122 &&
               r <= 150 &&
               b <= 170 &&
               g >= r + 28 &&
               g >= b + 12;
    }

    private static bool IsOwChatOrangeInline(byte r, byte g, byte b)
    {
        return r >= 145 &&
               g >= 74 &&
               g <= 210 &&
               b <= 150 &&
               r >= b + 52 &&
               r + 20 >= g;
    }

    private static int ComputeOtsuThreshold(int[] histogram, int totalPixels)
    {
        double sumAll = 0;
        for (int i = 0; i < 256; i++)
        {
            sumAll += i * histogram[i];
        }

        double weightBackground = 0;
        double sumBackground = 0;
        double maxVariance = 0;
        int threshold = 128;

        for (int t = 0; t < 256; t++)
        {
            weightBackground += histogram[t];
            if (weightBackground == 0)
            {
                continue;
            }

            double weightForeground = totalPixels - weightBackground;
            if (weightForeground == 0)
            {
                break;
            }

            sumBackground += t * histogram[t];
            double meanBackground = sumBackground / weightBackground;
            double meanForeground = (sumAll - sumBackground) / weightForeground;
            double variance = weightBackground * weightForeground *
                              (meanBackground - meanForeground) * (meanBackground - meanForeground);

            if (variance > maxVariance)
            {
                maxVariance = variance;
                threshold = t;
            }
        }

        return threshold;
    }
}
