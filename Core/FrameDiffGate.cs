using System.Drawing;

namespace OwTranslateLite.Core;

public sealed class FrameDiffGate
{
    private const int SampleStep = 8;
    private const int PerSampleChangeThreshold = 20;
    private const int MinimumChangedSamples = 3;
    private const double AverageChangeThreshold = 1.2;

    private byte[]? _previousSignature;
    private int _previousWidth;
    private int _previousHeight;

    public FrameDiffResult Observe(Bitmap bitmap)
    {
        byte[] signature = BuildSignature(bitmap);
        bool dimensionsChanged = bitmap.Width != _previousWidth || bitmap.Height != _previousHeight;
        if (_previousSignature is null || dimensionsChanged || _previousSignature.Length != signature.Length)
        {
            Store(signature, bitmap);
            return new FrameDiffResult(true, 0, 0, "initial");
        }

        int changedSamples = 0;
        int totalDelta = 0;
        for (int i = 0; i < signature.Length; i++)
        {
            int delta = Math.Abs(signature[i] - _previousSignature[i]);
            totalDelta += delta;
            if (delta >= PerSampleChangeThreshold)
            {
                changedSamples++;
            }
        }

        double averageDelta = signature.Length == 0 ? 0 : (double)totalDelta / signature.Length;
        bool changed = changedSamples >= MinimumChangedSamples || averageDelta >= AverageChangeThreshold;
        Store(signature, bitmap);
        return new FrameDiffResult(changed, changedSamples, averageDelta, changed ? "changed" : "stable");
    }

    public void Reset()
    {
        _previousSignature = null;
        _previousWidth = 0;
        _previousHeight = 0;
    }

    private void Store(byte[] signature, Bitmap bitmap)
    {
        _previousSignature = signature;
        _previousWidth = bitmap.Width;
        _previousHeight = bitmap.Height;
    }

    private static byte[] BuildSignature(Bitmap bitmap)
    {
        int columns = Math.Max(1, (bitmap.Width + SampleStep - 1) / SampleStep);
        int rows = Math.Max(1, (bitmap.Height + SampleStep - 1) / SampleStep);
        byte[] signature = new byte[columns * rows];
        int index = 0;

        for (int y = SampleStep / 2; y < bitmap.Height; y += SampleStep)
        {
            for (int x = SampleStep / 2; x < bitmap.Width; x += SampleStep)
            {
                Color color = bitmap.GetPixel(x, y);
                signature[index++] = ToLuminance(color);
            }
        }

        return index == signature.Length
            ? signature
            : signature[..index];
    }

    private static byte ToLuminance(Color color) =>
        (byte)((color.R * 299 + color.G * 587 + color.B * 114) / 1000);
}

public sealed record FrameDiffResult(
    bool HasChanged,
    int ChangedSamples,
    double AverageDelta,
    string Reason);
