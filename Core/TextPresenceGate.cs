using System.Drawing;

namespace OwTranslateLite.Core;

public sealed class TextPresenceGate
{
    private const int SampleStep = 2;
    private const int MinimumComponents = 3;
    private const int MinimumLines = 1;
    private const double MinimumScore = 42;

    public TextPresenceResult Observe(Bitmap bitmap)
    {
        int width = Math.Max(1, (bitmap.Width + SampleStep - 1) / SampleStep);
        int height = Math.Max(1, (bitmap.Height + SampleStep - 1) / SampleStep);
        int[] luminance = new int[width * height];
        int[] saturation = new int[width * height];
        bool[] mask = new bool[width * height];
        int maskPixels = 0;

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

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                if (!IsLikelyGlyphPixel(luminance, saturation, width, height, x, y))
                {
                    continue;
                }

                mask[index] = true;
                maskPixels++;
            }
        }

        List<TextComponent> components = FindComponents(mask, width, height);
        List<TextComponent> candidates = components
            .Where(static component => component.IsLikelyGlyph)
            .ToList();
        int lineCount = CountTextLines(candidates);
        double density = (double)maskPixels / Math.Max(1, width * height);
        double score = Math.Min(100, candidates.Count * 7 + lineCount * 28 + Math.Min(18, density * 900));
        bool hasText = candidates.Count >= MinimumComponents &&
                       lineCount >= MinimumLines &&
                       score >= MinimumScore;
        string reason = hasText
            ? "likely-text"
            : candidates.Count == 0
                ? "no-components"
                : $"weak-components-{candidates.Count}-lines-{lineCount}";

        return new TextPresenceResult(hasText, score, candidates.Count, lineCount, reason);
    }

    private static bool IsLikelyGlyphPixel(int[] luminance, int[] saturation, int width, int height, int x, int y)
    {
        int index = y * width + x;
        int currentLuminance = luminance[index];
        int currentSaturation = saturation[index];

        bool brightGlyph = currentLuminance >= 178 && currentSaturation <= 105;
        bool coloredGlyph = currentLuminance >= 112 && currentSaturation >= 48;
        bool contrastGlyph = currentLuminance >= 120 && GetLocalContrast(luminance, width, height, x, y, currentLuminance) >= 62;
        return brightGlyph || coloredGlyph || contrastGlyph;
    }

    private static int GetLocalContrast(int[] luminance, int width, int height, int x, int y, int currentLuminance)
    {
        int maxDelta = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            int sampleY = Math.Clamp(y + dy, 0, height - 1);
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                int sampleX = Math.Clamp(x + dx, 0, width - 1);
                maxDelta = Math.Max(maxDelta, Math.Abs(currentLuminance - luminance[sampleY * width + sampleX]));
            }
        }

        return maxDelta;
    }

    private static List<TextComponent> FindComponents(bool[] mask, int width, int height)
    {
        bool[] visited = new bool[mask.Length];
        List<TextComponent> components = [];
        Queue<int> queue = new();
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

    private static int CountTextLines(IReadOnlyList<TextComponent> components)
    {
        if (components.Count < MinimumComponents)
        {
            return 0;
        }

        List<double> rowCenters = components
            .Select(static component => component.CenterY)
            .OrderBy(static value => value)
            .ToList();
        int lines = 0;
        List<double> current = [];
        foreach (double center in rowCenters)
        {
            if (current.Count == 0 || Math.Abs(current.Average() - center) <= 5)
            {
                current.Add(center);
                continue;
            }

            if (current.Count >= MinimumComponents)
            {
                lines++;
            }

            current.Clear();
            current.Add(center);
        }

        if (current.Count >= MinimumComponents)
        {
            lines++;
        }

        return lines;
    }

    private static int ToLuminance(Color color) =>
        (color.R * 299 + color.G * 587 + color.B * 114) / 1000;

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
                if (originalWidth < 2 || originalHeight < 4 || originalHeight > 42)
                {
                    return false;
                }

                if (originalWidth > 110 || originalArea > 1400)
                {
                    return false;
                }

                double aspect = (double)originalWidth / Math.Max(1, originalHeight);
                return aspect is >= 0.08 and <= 8.0 && originalArea >= 6;
            }
        }
    }
}

public sealed record TextPresenceResult(
    bool HasLikelyText,
    double Score,
    int CandidateComponentCount,
    int CandidateLineCount,
    string Reason);
