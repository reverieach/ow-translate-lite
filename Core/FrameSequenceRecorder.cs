using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace OwTranslateLite.Core;

public sealed class FrameSequenceRecorder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _lock = new();
    private int _frameIndex;
    private DateTime _startedAt;

    public bool IsRecording { get; private set; }
    public string? SessionDirectory { get; private set; }
    public string? CaseId { get; private set; }

    public static string SessionsRoot => Path.Combine(FindRepoRoot(AppContext.BaseDirectory), "captured-screenshots", "sessions");

    public string Start(string caseId, CaptureRegion? captureRegion, int captureIntervalMs)
    {
        lock (_lock)
        {
            if (IsRecording && SessionDirectory is not null)
            {
                return SessionDirectory;
            }

            CaseId = string.IsNullOrWhiteSpace(caseId) ? "manual" : caseId.Trim();
            _startedAt = DateTime.Now;
            _frameIndex = 0;
            string sessionName = $"{_startedAt:yyyyMMdd-HHmmss}-{MakeSafeFileName(CaseId)}";
            SessionDirectory = Path.Combine(SessionsRoot, sessionName);
            Directory.CreateDirectory(SessionDirectory);
            Directory.CreateDirectory(Path.Combine(SessionDirectory, "frames"));

            FrameSequenceMetadata metadata = new(
                CaseId,
                _startedAt,
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                captureRegion,
                captureIntervalMs);
            WriteJson(Path.Combine(SessionDirectory, "session.json"), metadata);
            IsRecording = true;
            return SessionDirectory;
        }
    }

    public string? Stop()
    {
        lock (_lock)
        {
            IsRecording = false;
            return SessionDirectory;
        }
    }

    public void RecordFrame(
        Bitmap bitmap,
        Rect captureRegion,
        IReadOnlyList<OcrTextLine> rawOcrLines,
        IReadOnlyList<OcrTextLine> processedOcrLines,
        IReadOnlyList<ParsedChatLine> parsedLines,
        FrameDetectionResult detectionResult)
    {
        string? sessionDirectory;
        string? caseId;
        int frameIndex;
        DateTime timestamp = DateTime.Now;
        lock (_lock)
        {
            if (!IsRecording || SessionDirectory is null)
            {
                return;
            }

            sessionDirectory = SessionDirectory;
            caseId = CaseId;
            frameIndex = ++_frameIndex;
        }

        try
        {
            string frameStem = $"frame_{frameIndex:000000}";
            string imageFile = $"frames/{frameStem}.png";
            string imagePath = Path.Combine(sessionDirectory, "frames", $"{frameStem}.png");
            bitmap.Save(imagePath, ImageFormat.Png);

            FrameSequenceFrame frame = new(
                frameIndex,
                caseId ?? "manual",
                timestamp,
                (long)(timestamp - _startedAt).TotalMilliseconds,
                imageFile,
                FrameSequenceRect.FromRect(captureRegion),
                rawOcrLines.Select(FrameSequenceOcrLine.FromLine).ToArray(),
                processedOcrLines.Select(FrameSequenceOcrLine.FromLine).ToArray(),
                parsedLines.Select(FrameSequenceParsedLine.FromLine).ToArray(),
                detectionResult.CandidateLines.Select(FrameSequenceParsedLine.FromLine).ToArray(),
                detectionResult.Decisions.Select(FrameSequenceDecision.FromDecision).ToArray(),
                detectionResult.NewLines.Select(FrameSequenceParsedLine.FromLine).ToArray(),
                detectionResult.HasVisibleChat,
                detectionResult.ChatCycleJustReset);

            WriteJson(Path.Combine(sessionDirectory, "frames", $"{frameStem}.json"), frame);
        }
        catch
        {
            // Recording is diagnostic-only and must not interrupt OCR or translation.
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static string FindRepoRoot(string startDirectory)
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

    private static string MakeSafeFileName(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char ch in value)
        {
            builder.Append(Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }
}

public sealed record FrameSequenceMetadata(
    string CaseId,
    DateTime StartedAt,
    string AppVersion,
    CaptureRegion? CaptureRegion,
    int CaptureIntervalMs);

public sealed record FrameSequenceFrame(
    int FrameIndex,
    string CaseId,
    DateTime Timestamp,
    long ElapsedMs,
    string ImageFile,
    FrameSequenceRect CaptureRegion,
    IReadOnlyList<FrameSequenceOcrLine> RawOcrLines,
    IReadOnlyList<FrameSequenceOcrLine> ProcessedOcrLines,
    IReadOnlyList<FrameSequenceParsedLine> ParsedLines,
    IReadOnlyList<FrameSequenceParsedLine> CandidateLines,
    IReadOnlyList<FrameSequenceDecision> Decisions,
    IReadOnlyList<FrameSequenceParsedLine> NewLines,
    bool HasVisibleChat,
    bool ChatCycleJustReset);

public sealed record FrameSequenceOcrLine(string Text, FrameSequenceRect Bounds)
{
    public static FrameSequenceOcrLine FromLine(OcrTextLine line) =>
        new(line.Text, FrameSequenceRect.FromRect(line.Bounds));

    public OcrTextLine ToLine() => new(Text, Bounds.ToRect());
}

public sealed record FrameSequenceParsedLine(
    string Speaker,
    string SourceText,
    FrameSequenceRect Bounds,
    IReadOnlyList<string> GlossaryHits)
{
    public static FrameSequenceParsedLine FromLine(ParsedChatLine line) =>
        new(
            line.Speaker,
            line.SourceText,
            FrameSequenceRect.FromRect(line.Bounds),
            line.GlossaryHits.Select(static hit => $"{hit.Source}->{hit.Target}").ToArray());
}

public sealed record FrameSequenceDecision(
    string Speaker,
    string SourceText,
    FrameSequenceRect Bounds,
    bool Accepted,
    string Reason,
    string Key)
{
    public static FrameSequenceDecision FromDecision(FrameDetectionDecision decision) =>
        new(
            decision.Line.Speaker,
            decision.Line.SourceText,
            FrameSequenceRect.FromRect(decision.Line.Bounds),
            decision.Accepted,
            decision.Reason,
            decision.Key);
}

public sealed record FrameSequenceRect(double Left, double Top, double Width, double Height)
{
    public static FrameSequenceRect FromRect(Rect rect) =>
        new(rect.Left, rect.Top, rect.Width, rect.Height);

    public Rect ToRect() => new(Left, Top, Width, Height);
}
