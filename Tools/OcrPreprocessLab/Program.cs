using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using OwTranslateLite.Core;
using OwTranslateLite.Ocr;

Console.OutputEncoding = Encoding.UTF8;

string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
string inputDir = args.Length >= 1
    ? Path.GetFullPath(args[0])
    : Path.Combine(repoRoot, "ow-screenshot");
string outputDir = args.Length >= 2
    ? Path.GetFullPath(args[1])
    : Path.Combine(repoRoot, "Docs", "ocr-lab-output", DateTime.Now.ToString("yyyyMMdd-HHmmss"));

Directory.CreateDirectory(outputDir);
string previewDir = Path.Combine(outputDir, "previews");
Directory.CreateDirectory(previewDir);

if (!Directory.Exists(inputDir))
{
    Console.Error.WriteLine($"Input directory not found: {inputDir}");
    Environment.ExitCode = 2;
    return;
}

string[] imagePaths = Directory.EnumerateFiles(inputDir)
    .Where(IsSupportedImage)
    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
    .ToArray();

if (imagePaths.Length == 0)
{
    Console.Error.WriteLine($"No png/jpg/bmp images found in: {inputDir}");
    Environment.ExitCode = 2;
    return;
}

OcrPreprocessingMode[] modes =
[
    OcrPreprocessingMode.ColorPreserving,
    OcrPreprocessingMode.OwChatCyanMask,
    OcrPreprocessingMode.OwChatMultiColorMask,
    OcrPreprocessingMode.OwChatMultiColorMaskThickened
];

List<LabResult> results = [];
OwChatParser parser = new(OwGlossaryService.LoadDefault());
using OneOcrEngine engine = new();
foreach (string imagePath in imagePaths)
{
    using Bitmap source = new(imagePath);
    foreach (OcrPreprocessingMode mode in modes)
    {
        string imageName = Path.GetFileNameWithoutExtension(imagePath);
        string previewPath = Path.Combine(previewDir, $"{MakeSafeFileName(imageName)}.{mode}.png");
        using (Bitmap preview = OcrImagePreprocessor.Prepare(source, mode))
        {
            preview.Save(previewPath, ImageFormat.Png);
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyList<OcrTextLine> lines = await engine.RecognizeAsync(source, "auto", mode, CancellationToken.None);
        stopwatch.Stop();

        IReadOnlyList<OcrTextLine> processedLines = OcrTextPostProcessor.Process(lines);
        IReadOnlyList<ParsedChatLine> parsedLines = parser.Parse(processedLines);
        IReadOnlyList<string> effectiveLines = lines
            .Select(line => line.Text.Trim())
            .Where(IsEffectiveLine)
            .ToArray();
        results.Add(new LabResult(
            imagePath,
            mode,
            stopwatch.Elapsed,
            lines.Select(line => line.Text.Trim()).Where(static text => text.Length > 0).ToArray(),
            processedLines.Select(line => line.Text.Trim()).Where(static text => text.Length > 0).ToArray(),
            parsedLines.Select(static line => $"[{line.Speaker}]: {line.SourceText}").ToArray(),
            effectiveLines,
            previewPath));

        Console.WriteLine($"{Path.GetFileName(imagePath)} | {mode} | {stopwatch.ElapsedMilliseconds} ms | lines={lines.Count} parsed={parsedLines.Count} effective={effectiveLines.Count}");
    }
}

string reportPath = Path.Combine(outputDir, "report.md");
File.WriteAllText(reportPath, BuildReport(inputDir, outputDir, results), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine();
Console.WriteLine($"Report: {reportPath}");
Console.WriteLine($"Suggested overall mode: {GetSuggestedMode(results)}");

static string FindRepoRoot(string startDirectory)
{
    DirectoryInfo? directory = new(startDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "OwTranslateLite.csproj")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
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

static string BuildReport(string inputDir, string outputDir, IReadOnlyList<LabResult> results)
{
    StringBuilder builder = new();
    builder.AppendLine("# OW OCR Preprocess Lab");
    builder.AppendLine();
    builder.AppendLine($"- Input: `{inputDir}`");
    builder.AppendLine($"- Generated: `{DateTime.Now:yyyy-MM-dd HH:mm:ss}`");
    builder.AppendLine($"- Suggested overall mode: `{GetSuggestedMode(results)}`");
    builder.AppendLine();
    builder.AppendLine("| Image | Mode | Time | OCR Lines | Processed Lines | Parsed Chat | Effective Lines | Score | Preview |");
    builder.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |");

    foreach (LabResult result in results)
    {
        string preview = Path.GetRelativePath(outputDir, result.PreviewPath).Replace('\\', '/');
        builder.AppendLine($"| {EscapePipe(Path.GetFileName(result.ImagePath))} | `{result.Mode}` | {result.Elapsed.TotalMilliseconds:0} ms | {result.RawLines.Count} | {result.ProcessedLines.Count} | {result.ParsedChatLines.Count} | {result.EffectiveLines.Count} | {GetScore(result):0.0} | [{Path.GetFileName(result.PreviewPath)}]({preview}) |");
    }

    foreach (IGrouping<string, LabResult> imageGroup in results.GroupBy(static result => result.ImagePath))
    {
        builder.AppendLine();
        builder.AppendLine($"## {Path.GetFileName(imageGroup.Key)}");
        builder.AppendLine();
        builder.AppendLine($"Suggested mode: `{GetSuggestedMode(imageGroup)}`");
        foreach (LabResult result in imageGroup.OrderByDescending(GetScore))
        {
            builder.AppendLine();
            builder.AppendLine($"### {result.Mode}");
            builder.AppendLine();
            builder.AppendLine($"- Time: `{result.Elapsed.TotalMilliseconds:0} ms`");
            builder.AppendLine($"- OCR lines: `{result.RawLines.Count}`");
            builder.AppendLine($"- Processed lines: `{result.ProcessedLines.Count}`");
            builder.AppendLine($"- Parsed chat lines: `{result.ParsedChatLines.Count}`");
            builder.AppendLine($"- Effective lines: `{result.EffectiveLines.Count}`");
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
        .First().Mode.ToString();
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

internal sealed record LabResult(
    string ImagePath,
    OcrPreprocessingMode Mode,
    TimeSpan Elapsed,
    IReadOnlyList<string> RawLines,
    IReadOnlyList<string> ProcessedLines,
    IReadOnlyList<string> ParsedChatLines,
    IReadOnlyList<string> EffectiveLines,
    string PreviewPath);
