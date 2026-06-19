using System.Diagnostics;
using System.Drawing;

namespace OcrPreprocessLab;

internal sealed class StableTextLineGate
{
    private const int SampleStep = 4;
    private const int StableLuminanceThreshold = 22;
    private const int LocalContrastThreshold = 34;
    private const int MinimumComponentsPerLine = 3;
    private const int MinimumOriginalLineSpan = 56;
    private const double MinimumScore = 52;

    private SampledFrame? _previous;

    public StableTextLineGateResult Observe(Bitmap bitmap)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        SampledFrame current = Sample(bitmap);
        if (_previous is null ||
            _previous.Width != current.Width ||
            _previous.Height != current.Height)
        {
            _previous = current;
            stopwatch.Stop();
            return new StableTextLineGateResult(
                false,
                0,
                0,
                0,
                0,
                "initial-frame",
                stopwatch.Elapsed);
        }

        bool[] stableTextMask = BuildStableTextMask(_previous, current);
        List<TextComponent> components = FindComponents(stableTextMask, current.Width, current.Height)
            .Where(static component => component.IsLikelyGlyph)
            .ToList();
        TextLineSummary lines = CountTextLines(components);
        double stableDensity = stableTextMask.Count(static value => value) / (double)Math.Max(1, stableTextMask.Length);
        double score = Math.Min(
            100,
            components.Count * 4.5 +
            lines.LineCount * 30 +
            Math.Min(18, stableDensity * 1200) +
            Math.Min(16, lines.WidestOriginalSpan / 12.0));
        bool triggered = lines.LineCount > 0 &&
                         components.Count >= MinimumComponentsPerLine &&
                         score >= MinimumScore;
        string reason = triggered
            ? "stable-text-line"
            : components.Count == 0
                ? "no-stable-components"
                : $"weak-stable-components-{components.Count}-lines-{lines.LineCount}";

        _previous = current;
        stopwatch.Stop();
        return new StableTextLineGateResult(
            triggered,
            score,
            components.Count,
            lines.LineCount,
            stableDensity,
            reason,
            stopwatch.Elapsed);
    }

    public void Reset() => _previous = null;

    private static SampledFrame Sample(Bitmap bitmap)
    {
        int width = Math.Max(1, (bitmap.Width + SampleStep - 1) / SampleStep);
        int height = Math.Max(1, (bitmap.Height + SampleStep - 1) / SampleStep);
        int[] luminance = new int[width * height];
        int[] saturation = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            int sourceY = Math.Min(bitmap.Height - 1, y * SampleStep);
            for (int x = 0; x < width; x++)
            {
                int sourceX = Math.Min(bitmap.Width - 1, x * SampleStep);
                Color color = bitmap.GetPixel(sourceX, sourceY);
                int index = y * width + x;
                luminance[index] = ToLuminance(color);
                saturation[index] = Math.Max(color.R, Math.Max(color.G, color.B)) -
                                    Math.Min(color.R, Math.Min(color.G, color.B));
            }
        }

        return new SampledFrame(width, height, luminance, saturation);
    }

    private static bool[] BuildStableTextMask(SampledFrame previous, SampledFrame current)
    {
        bool[] mask = new bool[current.Width * current.Height];
        for (int y = 0; y < current.Height; y++)
        {
            for (int x = 0; x < current.Width; x++)
            {
                int index = y * current.Width + x;
                int delta = Math.Abs(current.Luminance[index] - previous.Luminance[index]);
                if (delta > StableLuminanceThreshold)
                {
                    continue;
                }

                if (!IsLikelyGlyphPixel(current, x, y))
                {
                    continue;
                }

                mask[index] = true;
            }
        }

        return mask;
    }

    private static bool IsLikelyGlyphPixel(SampledFrame frame, int x, int y)
    {
        int index = y * frame.Width + x;
        int luminance = frame.Luminance[index];
        int saturation = frame.Saturation[index];

        bool whiteOrPaleText = luminance >= 168 && saturation <= 125;
        bool coloredChatText = luminance >= 110 && saturation >= 55;
        bool outlinedText = luminance >= 118 &&
                            GetLocalContrast(frame, x, y, luminance) >= LocalContrastThreshold;
        return whiteOrPaleText || coloredChatText || outlinedText;
    }

    private static int GetLocalContrast(SampledFrame frame, int x, int y, int currentLuminance)
    {
        int maxDelta = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            int sampleY = Math.Clamp(y + dy, 0, frame.Height - 1);
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                int sampleX = Math.Clamp(x + dx, 0, frame.Width - 1);
                maxDelta = Math.Max(maxDelta, Math.Abs(currentLuminance - frame.Luminance[sampleY * frame.Width + sampleX]));
            }
        }

        return maxDelta;
    }

    private static List<TextComponent> FindComponents(bool[] mask, int width, int height)
    {
        bool[] visited = new bool[mask.Length];
        Queue<int> queue = new();
        List<TextComponent> components = [];
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || visited[i])
            {
                continue;
            }

            int minX = width;
            int minY = height;
            int maxX = 0;
            int maxY = 0;
            int area = 0;
            visited[i] = true;
            queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                int x = current % width;
                int y = current / width;
                area++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);

                TryVisit(x - 1, y);
                TryVisit(x + 1, y);
                TryVisit(x, y - 1);
                TryVisit(x, y + 1);

                void TryVisit(int nx, int ny)
                {
                    if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                    {
                        return;
                    }

                    int next = ny * width + nx;
                    if (!mask[next] || visited[next])
                    {
                        return;
                    }

                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }

            components.Add(new TextComponent(minX, minY, maxX, maxY, area));
        }

        return components;
    }

    private static TextLineSummary CountTextLines(IReadOnlyList<TextComponent> components)
    {
        List<List<TextComponent>> groups = [];
        foreach (TextComponent component in components.OrderBy(static component => component.CenterY))
        {
            List<TextComponent>? group = groups.FirstOrDefault(existing =>
                Math.Abs(existing.Average(static item => item.CenterY) - component.CenterY) <= 2.5);
            if (group is null)
            {
                groups.Add([component]);
            }
            else
            {
                group.Add(component);
            }
        }

        int lineCount = 0;
        int widestSpan = 0;
        foreach (List<TextComponent> group in groups)
        {
            if (group.Count < MinimumComponentsPerLine)
            {
                continue;
            }

            int span = (group.Max(static item => item.MaxX) - group.Min(static item => item.MinX) + 1) * SampleStep;
            if (span < MinimumOriginalLineSpan)
            {
                continue;
            }

            lineCount++;
            widestSpan = Math.Max(widestSpan, span);
        }

        return new TextLineSummary(lineCount, widestSpan);
    }

    private static int ToLuminance(Color color) =>
        (color.R * 299 + color.G * 587 + color.B * 114) / 1000;

    private sealed record SampledFrame(int Width, int Height, int[] Luminance, int[] Saturation);

    private sealed record TextLineSummary(int LineCount, int WidestOriginalSpan);

    private sealed record TextComponent(int MinX, int MinY, int MaxX, int MaxY, int Area)
    {
        public int Width => MaxX - MinX + 1;
        public int Height => MaxY - MinY + 1;
        public double CenterY => (MinY + MaxY) / 2.0;

        public bool IsLikelyGlyph
        {
            get
            {
                int originalWidth = Width * SampleStep;
                int originalHeight = Height * SampleStep;
                int originalArea = Area * SampleStep * SampleStep;
                if (originalWidth < 2 || originalHeight < 5 || originalHeight > 36)
                {
                    return false;
                }

                if (originalWidth > 96 || originalArea > 1100)
                {
                    return false;
                }

                double aspect = (double)originalWidth / Math.Max(1, originalHeight);
                return aspect is >= 0.08 and <= 7.5 && originalArea >= 8;
            }
        }
    }
}

internal sealed record StableTextLineGateResult(
    bool HasLikelyStableText,
    double Score,
    int CandidateComponentCount,
    int CandidateLineCount,
    double StableTextDensity,
    string Reason,
    TimeSpan Elapsed);
