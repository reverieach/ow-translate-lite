using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using OwTranslateLite.Core;
using Rect = System.Windows.Rect;

namespace OwTranslateLite.Ocr;

public sealed partial class OneOcrEngine : IOcrEngine, IDisposable
{
    private OcrNativeHandle? _context;
    private OcrNativeHandle? _pipeline;
    private OcrNativeHandle? _processOptions;
    private bool _ready;
    private bool _nativePathConfigured;

    public string Name => "OneOCR";

    public async Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(Bitmap bitmap, string languageCode, CancellationToken cancellationToken)
    {
        return await RecognizeAsync(bitmap, languageCode, OcrImagePreprocessor.DefaultMode, cancellationToken);
    }

    public async Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(
        Bitmap bitmap,
        string languageCode,
        OcrPreprocessingMode preprocessingMode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureInitializedAsync();
        if (!_ready)
        {
            return Array.Empty<OcrTextLine>();
        }

        using Bitmap prepared = OcrImagePreprocessor.Prepare(bitmap, preprocessingMode);
        BitmapData data = prepared.LockBits(new Rectangle(0, 0, prepared.Width, prepared.Height), ImageLockMode.ReadOnly, prepared.PixelFormat);
        long instance = 0;
        try
        {
            Img img = new()
            {
                t = 3,
                col = prepared.Width,
                row = prepared.Height,
                _unk = 0,
                step = Image.GetPixelFormatSize(prepared.PixelFormat) / 8 * prepared.Width,
                data_ptr = data.Scan0
            };

            long res = OneOcrNative.RunOcrPipeline(_pipeline!.Value, ref img, _processOptions!.Value, out instance);
            if (res != 0)
            {
                return Array.Empty<OcrTextLine>();
            }

            res = OneOcrNative.GetOcrLineCount(instance, out long lineCount);
            if (res != 0)
            {
                return Array.Empty<OcrTextLine>();
            }

            List<OcrTextLine> lines = [];
            for (long i = 0; i < lineCount; i++)
            {
                if (OneOcrNative.GetOcrLine(instance, i, out long line) != 0 || line == 0)
                {
                    continue;
                }

                if (OneOcrNative.GetOcrLineContent(line, out IntPtr contentPtr) != 0)
                {
                    continue;
                }

                string text = PtrToUtf8(contentPtr).Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (OneOcrNative.GetOcrLineBoundingBox(line, out IntPtr boxPtr) == 0)
                {
                    BoundingBox box = Marshal.PtrToStructure<BoundingBox>(boxPtr);
                    double left = Math.Min(Math.Min(box.x1, box.x2), Math.Min(box.x3, box.x4));
                    double top = Math.Min(Math.Min(box.y1, box.y2), Math.Min(box.y3, box.y4));
                    double right = Math.Max(Math.Max(box.x1, box.x2), Math.Max(box.x3, box.x4));
                    double bottom = Math.Max(Math.Max(box.y1, box.y2), Math.Max(box.y3, box.y4));
                    Rect bounds = OcrImagePreprocessor.ScaleBoundsBack(new Rect(left, top, right - left, bottom - top));
                    lines.Add(new OcrTextLine(text, bounds));
                }
            }

            return lines;
        }
        finally
        {
            if (instance != 0)
            {
                _ = OneOcrNative.ReleaseOcrResult(instance);
            }

            prepared.UnlockBits(data);
        }
    }

    private Task EnsureInitializedAsync()
    {
        if (_ready)
        {
            return Task.CompletedTask;
        }

        ConfigureNativePath();
        try
        {
            if (OneOcrNative.CreateOcrInitOptions(out long context) != 0)
            {
                return Task.CompletedTask;
            }

            _context = new OcrNativeHandle(context, OcrNativeHandleKind.InitOptions);
            _ = OneOcrNative.OcrInitOptionsSetUseModelDelayLoad(_context.Value, 1);
            string modelPath = Path.Combine(AppContext.BaseDirectory, "OneOcr", "oneocr.onemodel");
            string key = "kj)TGtrK>f]b[Piow.gU+nC@s\"\"\"\"\"\"4";
            if (OneOcrNative.CreateOcrPipeline(modelPath, key, _context.Value, out long pipeline) != 0)
            {
                Dispose();
                return Task.CompletedTask;
            }

            _pipeline = new OcrNativeHandle(pipeline, OcrNativeHandleKind.Pipeline);
            if (OneOcrNative.CreateOcrProcessOptions(out long processOptions) != 0)
            {
                Dispose();
                return Task.CompletedTask;
            }

            _processOptions = new OcrNativeHandle(processOptions, OcrNativeHandleKind.ProcessOptions);
            _ = OneOcrNative.OcrProcessOptionsSetMaxRecognitionLineCount(_processOptions.Value, 80);
            _ready = true;
        }
        catch
        {
            _ready = false;
            Dispose();
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _processOptions?.Dispose();
        _processOptions = null;
        _pipeline?.Dispose();
        _pipeline = null;
        _context?.Dispose();
        _context = null;
        _ready = false;
    }

    private void ConfigureNativePath()
    {
        if (_nativePathConfigured)
        {
            return;
        }

        string oneOcrDir = Path.Combine(AppContext.BaseDirectory, "OneOcr");
        if (Directory.Exists(oneOcrDir))
        {
            NativeMethods.SetDllDirectory(oneOcrDir);
        }

        _nativePathConfigured = true;
    }

    private static string PtrToUtf8(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
        {
            return "";
        }

        int length = 0;
        while (Marshal.ReadByte(ptr, length) != 0)
        {
            length++;
        }

        byte[] buffer = new byte[length];
        Marshal.Copy(ptr, buffer, 0, length);
        return Encoding.UTF8.GetString(buffer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Img
    {
        public int t;
        public int col;
        public int row;
        public int _unk;
        public long step;
        public IntPtr data_ptr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BoundingBox
    {
        public float x1;
        public float y1;
        public float x2;
        public float y2;
        public float x3;
        public float y3;
        public float x4;
        public float y4;
    }

    private sealed class OcrNativeHandle : SafeHandle
    {
        private readonly OcrNativeHandleKind _kind;

        public OcrNativeHandle(long value, OcrNativeHandleKind kind)
            : base(IntPtr.Zero, ownsHandle: true)
        {
            _kind = kind;
            SetHandle(new IntPtr(value));
        }

        public long Value => handle.ToInt64();

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            long value = handle.ToInt64();
            long result = _kind switch
            {
                OcrNativeHandleKind.ProcessOptions => OneOcrNative.ReleaseOcrProcessOptions(value),
                OcrNativeHandleKind.Pipeline => OneOcrNative.ReleaseOcrPipeline(value),
                OcrNativeHandleKind.InitOptions => OneOcrNative.ReleaseOcrInitOptions(value),
                _ => 0
            };
            return result == 0;
        }
    }

    private enum OcrNativeHandleKind
    {
        InitOptions,
        Pipeline,
        ProcessOptions
    }

    private static partial class OneOcrNative
    {
        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long CreateOcrInitOptions(out long ctx);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long OcrInitOptionsSetUseModelDelayLoad(long ctx, byte flag);

        [LibraryImport("oneocr.dll", StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long CreateOcrPipeline(string modelPath, string key, long ctx, out long pipeline);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long CreateOcrProcessOptions(out long opt);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long OcrProcessOptionsSetMaxRecognitionLineCount(long opt, long count);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long RunOcrPipeline(long pipeline, ref Img img, long opt, out long instance);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrLineCount(long instance, out long count);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrLine(long instance, long index, out long line);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrLineContent(long line, out IntPtr content);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrLineBoundingBox(long line, out IntPtr boundingBoxPtr);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long ReleaseOcrResult(long instance);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long ReleaseOcrProcessOptions(long opt);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long ReleaseOcrPipeline(long pipeline);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long ReleaseOcrInitOptions(long ctx);
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", EntryPoint = "SetDllDirectoryW", StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetDllDirectory(string lpPathName);
    }
}
