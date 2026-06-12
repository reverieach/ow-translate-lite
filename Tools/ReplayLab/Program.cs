using System.Text;
using System.Text.Json;
using System.IO;
using System.Windows;
using OwTranslateLite.Core;

Console.OutputEncoding = Encoding.UTF8;

JsonSerializerOptions jsonOptions = new()
{
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

if (args.Length >= 1 && string.Equals(args[0], "--timeline-smoke", StringComparison.OrdinalIgnoreCase))
{
    int failures = RunTimelineSmoke();
    Console.WriteLine($"Timeline smoke: failures={failures}");
    Environment.ExitCode = failures == 0 ? 0 : 1;
    return;
}

if (args.Length >= 2 && string.Equals(args[0], "--similarity", StringComparison.OrdinalIgnoreCase))
{
    string expectationPath = Path.GetFullPath(args[1]);
    SimilarityRegressionSet set = ReadJson<SimilarityRegressionSet>(expectationPath);
    int failures = 0;
    foreach (SimilarityCase item in set.TextCases)
    {
        double score = OcrDedupeNormalizer.TextSimilarityScore(
            OcrDedupeNormalizer.NormalizeText(item.Left),
            OcrDedupeNormalizer.NormalizeText(item.Right));
        bool passed = score >= item.MinScore && score <= item.MaxScore;
        if (item.ExpectedSimilar is bool expectedSimilar)
        {
            bool actualSimilar = OcrDedupeNormalizer.IsSimilarText(
                OcrDedupeNormalizer.NormalizeText(item.Left),
                OcrDedupeNormalizer.NormalizeText(item.Right));
            passed &= actualSimilar == expectedSimilar;
        }

        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} text {item.Id}: score={score:0.###}");
        if (!passed)
        {
            failures++;
        }
    }

    foreach (SpeakerMatchCase item in set.SpeakerCases)
    {
        bool actual = OcrDedupeNormalizer.IsSpeakerMatch(
            OcrDedupeNormalizer.NormalizeSpeaker(item.Left),
            OcrDedupeNormalizer.NormalizeSpeaker(item.Right));
        bool passed = actual == item.ExpectedMatch;
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} speaker {item.Id}: actual={actual}");
        if (!passed)
        {
            failures++;
        }
    }

    OwChatParser regressionParser = new(OwGlossaryService.LoadDefault());
    foreach (ParserCase item in set.ParserCases ?? Array.Empty<ParserCase>())
    {
        IReadOnlyList<ParsedChatLine> parsed = regressionParser.Parse([new OcrTextLine(item.LineText, new Rect(0, 0, 400, 24))]);
        IReadOnlyList<string> expectedKeys = item.ExpectedMessages.Select(ReplayKey.MessageKey).ToArray();
        IReadOnlyList<string> actualKeys = parsed
            .Select(static line => new ExpectedChatMessage(line.Speaker, line.SourceText))
            .Select(ReplayKey.MessageKey)
            .ToArray();
        bool passed = expectedKeys.SequenceEqual(actualKeys, StringComparer.Ordinal);
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} parser {item.Id}: parsed={parsed.Count}");
        if (!passed)
        {
            failures++;
        }
    }

    int parserCount = set.ParserCases?.Count ?? 0;
    Console.WriteLine($"Similarity regression: cases={set.TextCases.Count + set.SpeakerCases.Count + parserCount}, failures={failures}");
    Environment.ExitCode = failures == 0 ? 0 : 1;
    return;
}

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

static int RunTimelineSmoke()
{
    int failures = 0;
    ChatTimeline timeline = new(capacity: 2);
    ParsedChatLine first = new("Reverieach", "안녕", new Rect(0, 0, 100, 20), []);
    ParsedChatLine second = new("疯狂的鹿", "힐 줄게", new Rect(0, 24, 100, 20), []);
    ParsedChatLine third = new("天剑若叶", "겐지 뒤", new Rect(0, 48, 100, 20), []);

    ChatMessage firstMessage = timeline.AddDetected(first, frameId: 1);
    ChatMessage secondMessage = timeline.AddDetected(second, frameId: 2);
    ChatMessage thirdMessage = timeline.AddDetected(third, frameId: 3);
    failures += Assert(firstMessage.Seq == 1, "first seq");
    failures += Assert(secondMessage.Seq == 2, "second seq");
    failures += Assert(thirdMessage.Seq == 3, "third seq");
    failures += Assert(timeline.Messages.Count == 2, "capacity trim");
    failures += Assert(timeline.Messages[0].Seq == 2 && timeline.Messages[1].Seq == 3, "trim keeps tail");

    timeline.Observe(thirdMessage, new ParsedChatLine("天剑若叶", "겐지뒤", new Rect(0, 50, 120, 20), []), frameId: 4);
    failures += Assert(thirdMessage.SeenCount == 2, "observe seen count");
    failures += Assert(thirdMessage.Variants.Count == 2, "observe variant");
    failures += Assert(thirdMessage.LastSeenFrameId == 4, "observe frame");

    timeline.MarkQueued(thirdMessage);
    failures += Assert(thirdMessage.State == ChatMessageState.Queued, "queued state");
    timeline.MarkTranslating(thirdMessage);
    failures += Assert(thirdMessage.State == ChatMessageState.Translating, "translating state");
    timeline.MarkTranslated(thirdMessage, "源氏在后面");
    failures += Assert(thirdMessage.State == ChatMessageState.Translated && thirdMessage.Translation == "源氏在后面", "translated state");
    timeline.MarkFailed(secondMessage);
    failures += Assert(secondMessage.State == ChatMessageState.Failed && secondMessage.RetryCount == 1, "failed state");
    long retrySeq = secondMessage.Seq;
    timeline.MarkQueued(secondMessage);
    failures += Assert(secondMessage.State == ChatMessageState.Queued && secondMessage.Seq == retrySeq, "retry keeps seq");

    IReadOnlyList<ChatMessage> tail = timeline.TailWindow(1);
    failures += Assert(tail.Count == 1 && tail[0].Seq == 3, "tail window");
    timeline.Clear();
    failures += Assert(timeline.Messages.Count == 0, "clear");
    failures += Assert(timeline.AddDetected(first, frameId: 5).Seq == 1, "clear resets seq");
    failures += RunAlignmentSmoke();
    return failures;
}

static int RunAlignmentSmoke()
{
    int failures = 0;
    ChatTimeline timeline = new();
    TimelineAlignmentDetector detector = new();
    TimelineAlignmentResult cold = detector.Detect(
        timeline,
        [
            Parsed("Reverieach", "안녕"),
            Parsed("疯狂的鹿", "힐 줄게")
        ],
        frameId: 1);
    failures += Assert(cold.NewMessages.Count == 2 && cold.Matches.Count == 0, "align cold start all new");
    failures += Assert(timeline.Messages[0].Seq == 1 && timeline.Messages[1].Seq == 2, "align cold seq");

    TimelineAlignmentResult append = detector.Detect(
        timeline,
        [
            Parsed("Reverieach", "안녕"),
            Parsed("疯狂的鹿", "힐 줄게"),
            Parsed("天剑若叶", "겐지 뒤")
        ],
        frameId: 2);
    failures += Assert(append.Matches.Count == 2 && append.NewMessages.Count == 1, "align suffix append");
    failures += Assert(append.NewMessages[0].Seq == 3, "align append seq");

    detector = new TimelineAlignmentDetector();
    timeline = new ChatTimeline();
    detector.Detect(timeline, [Parsed("Reverieach", "ㄱㄱ")], frameId: 1);
    detector.Detect(timeline, [], frameId: 2);
    TimelineAlignmentResult afterEmpty = detector.Detect(timeline, [Parsed("Reverieach", "ㄱㄱ")], frameId: 3);
    failures += Assert(afterEmpty.Matches.Count == 0 && afterEmpty.NewMessages.Count == 1, "align after empty short forced new");
    failures += Assert(afterEmpty.NewMessages[0].Seq == 2, "align after empty seq");

    detector = new TimelineAlignmentDetector();
    timeline = new ChatTimeline();
    detector.Detect(
        timeline,
        [
            Parsed("Reverieach", "안녕하세요"),
            Parsed("疯狂的鹿", "오늘 첫 판이에요"),
            Parsed("天剑若叶", "위도우 조심")
        ],
        frameId: 1);
    detector.Detect(timeline, [], frameId: 2);
    TimelineAlignmentResult history = detector.Detect(
        timeline,
        [
            Parsed("Reverieach", "안녕하세요"),
            Parsed("疯狂的鹿", "오늘 첫 판이에요"),
            Parsed("天剑若叶", "위도우 조심")
        ],
        frameId: 3);
    failures += Assert(history.Matches.Count == 3 && history.NewMessages.Count == 0, "align after empty history");

    return failures;
}

static ParsedChatLine Parsed(string speaker, string text) =>
    new(speaker, text, new Rect(0, 0, 100, 20), []);

static int Assert(bool condition, string label)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} timeline {label}");
    return condition ? 0 : 1;
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

public sealed record SimilarityRegressionSet(
    IReadOnlyList<SimilarityCase> TextCases,
    IReadOnlyList<SpeakerMatchCase> SpeakerCases,
    IReadOnlyList<ParserCase>? ParserCases = null);

public sealed record SimilarityCase(
    string Id,
    string Left,
    string Right,
    double MinScore,
    double MaxScore,
    bool? ExpectedSimilar = null);

public sealed record SpeakerMatchCase(
    string Id,
    string Left,
    string Right,
    bool ExpectedMatch);

public sealed record ParserCase(
    string Id,
    string LineText,
    IReadOnlyList<ExpectedChatMessage> ExpectedMessages);
