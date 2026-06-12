using System.Text;
using System.Text.Json;
using System.IO;
using OwTranslateLite.Core;

Console.OutputEncoding = Encoding.UTF8;

JsonSerializerOptions jsonOptions = new()
{
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: ReplayLab <session-directory> [expected.json] [output-directory]");
    Environment.ExitCode = 2;
    return;
}

string sessionDirectory = Path.GetFullPath(args[0]);
if (!Directory.Exists(sessionDirectory))
{
    Console.Error.WriteLine($"Session directory not found: {sessionDirectory}");
    Environment.ExitCode = 2;
    return;
}

string framesDirectory = Path.Combine(sessionDirectory, "frames");
if (!Directory.Exists(framesDirectory))
{
    Console.Error.WriteLine($"Frames directory not found: {framesDirectory}");
    Environment.ExitCode = 2;
    return;
}

string? expectedPath = args.Length >= 2 ? Path.GetFullPath(args[1]) : null;
string outputDirectory = args.Length >= 3
    ? Path.GetFullPath(args[2])
    : Path.Combine(sessionDirectory, "replay-output", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(outputDirectory);

FrameSequenceMetadata? metadata = ReadMetadata(sessionDirectory);
ReplayExpectation? expectation = ReadExpectation(expectedPath);
string[] framePaths = Directory.EnumerateFiles(framesDirectory, "frame_*.json")
    .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
    .ToArray();

if (framePaths.Length == 0)
{
    Console.Error.WriteLine($"No frame_*.json files found in: {framesDirectory}");
    Environment.ExitCode = 2;
    return;
}

List<string> dedupeLog = [];
AppSettings settings = new()
{
    EnableDedupeDebugLog = true
};
OwGlossaryService glossary = OwGlossaryService.LoadDefault();
TranslationCoordinator coordinator = new(settings, glossary, message => dedupeLog.Add(message));
OwChatParser parser = new(glossary);
List<ReplayFrameTrace> traces = [];
List<ExpectedChatMessage> acceptedMessages = [];

foreach (string framePath in framePaths)
{
    FrameSequenceFrame frame = ReadJson<FrameSequenceFrame>(framePath);
    IReadOnlyList<OcrTextLine> rawLines = frame.RawOcrLines.Select(static line => line.ToLine()).ToArray();
    IReadOnlyList<OcrTextLine> processedLines = OcrTextPostProcessor.Process(rawLines);
    IReadOnlyList<ParsedChatLine> parsedLines = parser.Parse(processedLines);
    FrameDetectionResult detection = coordinator.DetectNewLinesFromParsedLines(parsedLines);

    foreach (ParsedChatLine line in detection.NewLines)
    {
        acceptedMessages.Add(new ExpectedChatMessage(line.Speaker, line.SourceText));
    }

    coordinator.CompleteOfflineTranslations(detection.NewLines);

    traces.Add(new ReplayFrameTrace(
        frame.FrameIndex,
        frame.ElapsedMs,
        frame.RawOcrLines.Select(static line => line.Text).ToArray(),
        processedLines.Select(static line => line.Text).ToArray(),
        parsedLines.Select(ReplayChatLine.FromParsed).ToArray(),
        detection.CandidateLines.Select(ReplayChatLine.FromParsed).ToArray(),
        detection.Decisions.Select(ReplayDecision.FromDecision).ToArray(),
        detection.NewLines.Select(ReplayChatLine.FromParsed).ToArray(),
        detection.HasVisibleChat,
        detection.ChatCycleJustReset));

    Console.WriteLine($"frame={frame.FrameIndex:000000} raw={rawLines.Count} parsed={parsedLines.Count} new={detection.NewLines.Count}");
}

ReplayMetrics metrics = expectation is null
    ? ReplayMetrics.FromActualOnly(acceptedMessages)
    : ReplayMetrics.Compare(expectation, acceptedMessages);

ReplayReport report = new(
    sessionDirectory,
    metadata?.CaseId ?? Path.GetFileName(sessionDirectory),
    framePaths.Length,
    acceptedMessages,
    expectation,
    metrics,
    traces,
    dedupeLog);

string tracePath = Path.Combine(outputDirectory, "trace.json");
File.WriteAllText(tracePath, JsonSerializer.Serialize(report, jsonOptions), new UTF8Encoding(false));

string reportPath = Path.Combine(outputDirectory, "report.md");
File.WriteAllText(reportPath, BuildMarkdownReport(report), new UTF8Encoding(false));

Console.WriteLine();
Console.WriteLine($"Trace: {tracePath}");
Console.WriteLine($"Report: {reportPath}");
Console.WriteLine($"Metrics: missing={metrics.MissingCount}, duplicates={metrics.DuplicateCount}, outOfOrder={metrics.OutOfOrderCount}, extra={metrics.ExtraCount}");

if (expectation is not null && !metrics.Passed)
{
    Environment.ExitCode = 1;
}

FrameSequenceMetadata? ReadMetadata(string directory)
{
    string path = Path.Combine(directory, "session.json");
    return File.Exists(path) ? ReadJson<FrameSequenceMetadata>(path) : null;
}

ReplayExpectation? ReadExpectation(string? path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return null;
    }

    if (!File.Exists(path))
    {
        throw new FileNotFoundException($"Expectation file not found: {path}", path);
    }

    return ReadJson<ReplayExpectation>(path);
}

T ReadJson<T>(string path)
{
    string json = File.ReadAllText(path, Encoding.UTF8);
    return JsonSerializer.Deserialize<T>(json, jsonOptions)
           ?? throw new InvalidOperationException($"Could not parse JSON: {path}");
}

static string BuildMarkdownReport(ReplayReport report)
{
    StringBuilder builder = new();
    builder.AppendLine("# ReplayLab Report");
    builder.AppendLine();
    builder.AppendLine($"- Session: `{report.SessionDirectory}`");
    builder.AppendLine($"- Case: `{report.CaseId}`");
    builder.AppendLine($"- Frames: `{report.FrameCount}`");
    builder.AppendLine($"- Accepted messages: `{report.ActualMessages.Count}`");
    builder.AppendLine($"- Missing: `{report.Metrics.MissingCount}`");
    builder.AppendLine($"- Duplicates: `{report.Metrics.DuplicateCount}`");
    builder.AppendLine($"- Out of order: `{report.Metrics.OutOfOrderCount}`");
    builder.AppendLine($"- Extra: `{report.Metrics.ExtraCount}`");
    builder.AppendLine($"- Passed: `{report.Metrics.Passed}`");
    builder.AppendLine();
    builder.AppendLine("## Accepted Messages");
    builder.AppendLine();
    builder.AppendLine("```text");
    foreach (ExpectedChatMessage message in report.ActualMessages)
    {
        builder.AppendLine($"[{message.Speaker}]: {message.SourceText}");
    }

    builder.AppendLine("```");
    builder.AppendLine();
    builder.AppendLine("## Frame Summary");
    builder.AppendLine();
    builder.AppendLine("| Frame | Elapsed ms | Raw | Parsed | New |");
    builder.AppendLine("| ---: | ---: | ---: | ---: | ---: |");
    foreach (ReplayFrameTrace frame in report.Frames)
    {
        builder.AppendLine($"| {frame.FrameIndex} | {frame.ElapsedMs} | {frame.RawOcrLines.Count} | {frame.ParsedLines.Count} | {frame.NewLines.Count} |");
    }

    return builder.ToString();
}

public sealed record ReplayExpectation(
    string CaseId,
    IReadOnlyList<ExpectedChatMessage> ExpectedMessages,
    int AllowedMissingCount = 0,
    int AllowedDuplicateCount = 0,
    int AllowedOutOfOrderCount = 0,
    int AllowedExtraCount = 0);

public sealed record ExpectedChatMessage(string Speaker, string SourceText);

public sealed record ReplayReport(
    string SessionDirectory,
    string CaseId,
    int FrameCount,
    IReadOnlyList<ExpectedChatMessage> ActualMessages,
    ReplayExpectation? Expectation,
    ReplayMetrics Metrics,
    IReadOnlyList<ReplayFrameTrace> Frames,
    IReadOnlyList<string> DedupeLog);

public sealed record ReplayFrameTrace(
    int FrameIndex,
    long ElapsedMs,
    IReadOnlyList<string> RawOcrLines,
    IReadOnlyList<string> ProcessedOcrLines,
    IReadOnlyList<ReplayChatLine> ParsedLines,
    IReadOnlyList<ReplayChatLine> CandidateLines,
    IReadOnlyList<ReplayDecision> Decisions,
    IReadOnlyList<ReplayChatLine> NewLines,
    bool HasVisibleChat,
    bool ChatCycleJustReset);

public sealed record ReplayChatLine(string Speaker, string SourceText)
{
    public static ReplayChatLine FromParsed(ParsedChatLine line) => new(line.Speaker, line.SourceText);
}

public sealed record ReplayDecision(string Speaker, string SourceText, bool Accepted, string Reason, string Key)
{
    public static ReplayDecision FromDecision(FrameDetectionDecision decision) =>
        new(decision.Line.Speaker, decision.Line.SourceText, decision.Accepted, decision.Reason, decision.Key);
}

public sealed record ReplayMetrics(
    int MissingCount,
    int DuplicateCount,
    int OutOfOrderCount,
    int ExtraCount,
    bool Passed)
{
    public static ReplayMetrics FromActualOnly(IReadOnlyList<ExpectedChatMessage> actual)
    {
        int duplicates = actual
            .GroupBy(ReplayKey.MessageKey, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Sum(static group => group.Count() - 1);

        return new ReplayMetrics(0, duplicates, 0, 0, true);
    }

    public static ReplayMetrics Compare(ReplayExpectation expectation, IReadOnlyList<ExpectedChatMessage> actual)
    {
        List<string> expectedKeys = expectation.ExpectedMessages.Select(ReplayKey.MessageKey).ToList();
        List<string> actualKeys = actual.Select(ReplayKey.MessageKey).ToList();
        Dictionary<string, int> expectedCounts = expectedKeys
            .GroupBy(static key => key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        Dictionary<string, int> actualCounts = actualKeys
            .GroupBy(static key => key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

        int missing = expectedCounts.Sum(item => Math.Max(0, item.Value - actualCounts.GetValueOrDefault(item.Key)));
        int duplicates = actualCounts.Sum(item => Math.Max(0, item.Value - expectedCounts.GetValueOrDefault(item.Key)));
        int extra = actualKeys.Count(key => !expectedCounts.ContainsKey(key));
        int outOfOrder = CountOutOfOrder(expectedKeys, actualKeys);
        bool passed = missing <= expectation.AllowedMissingCount &&
                      duplicates <= expectation.AllowedDuplicateCount &&
                      outOfOrder <= expectation.AllowedOutOfOrderCount &&
                      extra <= expectation.AllowedExtraCount;

        return new ReplayMetrics(missing, duplicates, outOfOrder, extra, passed);
    }

    private static int CountOutOfOrder(IReadOnlyList<string> expectedKeys, IReadOnlyList<string> actualKeys)
    {
        int outOfOrder = 0;
        int previousIndex = -1;
        foreach (string expectedKey in expectedKeys)
        {
            int index = FindIndex(actualKeys, expectedKey);
            if (index < 0)
            {
                continue;
            }

            if (index < previousIndex)
            {
                outOfOrder++;
            }

            previousIndex = Math.Max(previousIndex, index);
        }

        return outOfOrder;
    }

    private static int FindIndex(IReadOnlyList<string> values, string expected)
    {
        for (int index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], expected, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}

public static class ReplayKey
{
    public static string MessageKey(ExpectedChatMessage message) =>
        $"{OcrDedupeNormalizer.NormalizeSpeaker(message.Speaker)}:{OcrDedupeNormalizer.NormalizeText(message.SourceText)}";
}
