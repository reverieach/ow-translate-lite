using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using OwTranslateLite.Core;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace OwTranslateLite.Overlay;

public partial class OverlayWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;

    private AppSettings? _settings;
    private IReadOnlyList<TranslationRecord> _records = [];
    private bool _isClickThrough = true;

    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyClickThrough(_isClickThrough);
        FloatingPanel.MouseLeftButtonDown += FloatingPanel_MouseLeftButtonDown;
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _isClickThrough = settings.OverlayClickThrough;
        RecordList.FontSize = settings.OverlayFontSize;
        ApplyBackgroundOpacity(settings.OverlayOpacity);
        ApplyClickThrough(settings.OverlayClickThrough);
        ApplyModeLayout(settings);
        RenderRecords();
    }

    public void UpdateRecords(IReadOnlyList<TranslationRecord> records)
    {
        _records = records.ToList();
        RenderRecords();
    }

    public void MoveNear(Rect captureRegion)
    {
        if (_settings?.OverlayMode == "Inline")
        {
            MoveToCaptureRegion(captureRegion);
            return;
        }

        if (_settings?.OverlayLeft is double left && _settings.OverlayTop is double top)
        {
            Left = left;
            Top = top;
            Width = Math.Max(260, _settings.OverlayWidth ?? Width);
            Height = Math.Max(100, _settings.OverlayHeight ?? Height);
            return;
        }

        Rect dipRegion = ToDipRect(captureRegion);
        Left = dipRegion.Left;
        Top = Math.Max(0, dipRegion.Top - Height - 12);
        Width = Math.Max(420, dipRegion.Width);
    }

    private void ApplyModeLayout(AppSettings settings)
    {
        if (settings.OverlayMode == "Inline" && settings.CaptureRegion is not null)
        {
            FloatingPanel.Visibility = Visibility.Collapsed;
            ResizeMode = ResizeMode.NoResize;
            MoveToCaptureRegion(settings.CaptureRegion.ToRect());
        }
        else
        {
            FloatingPanel.Visibility = Visibility.Visible;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            if (settings.OverlayLeft is double left && settings.OverlayTop is double top)
            {
                Left = left;
                Top = top;
                Width = Math.Max(260, settings.OverlayWidth ?? Width);
                Height = Math.Max(100, settings.OverlayHeight ?? Height);
            }
        }
    }

    private void MoveToCaptureRegion(Rect captureRegion)
    {
        Rect dipRegion = ToDipRect(captureRegion);
        Left = dipRegion.Left;
        Top = dipRegion.Top;
        Width = Math.Max(80, dipRegion.Width);
        Height = Math.Max(30, dipRegion.Height);
    }

    private void RenderRecords()
    {
        RootCanvas.Children.Clear();
        RootCanvas.Children.Add(FloatingPanel);

        if (_settings?.OverlayMode == "Inline")
        {
            RecordList.ItemsSource = null;
            RenderInlineRecords();
        }
        else
        {
            FloatingPanel.Visibility = Visibility.Visible;
            RecordList.ItemsSource = _records.ToList();
        }
    }

    private void RenderInlineRecords()
    {
        if (_settings?.CaptureRegion is null)
        {
            return;
        }

        Rect captureRegion = _settings.CaptureRegion.ToRect();
        Rect captureDipRegion = ToDipRect(captureRegion);
        foreach (TranslationRecord record in _records.TakeLast(8))
        {
            Border label = CreateInlineLabel(record);
            Rect recordDipBounds = ToDipRect(record.ScreenBounds);
            double x = Math.Max(0, recordDipBounds.Left - captureDipRegion.Left);
            double y = Math.Max(0, recordDipBounds.Top - captureDipRegion.Top + recordDipBounds.Height + 2);
            label.MaxWidth = Math.Max(180, Width - x - 8);
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, Math.Min(Math.Max(0, Height - 36), y));
            RootCanvas.Children.Add(label);
        }
    }

    private Border CreateInlineLabel(TranslationRecord record)
    {
        TextBlock text = new()
        {
            Text = record.TranslatedText,
            Foreground = MediaBrushes.White,
            FontSize = _settings?.OverlayFontSize ?? 20,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };

        return new Border
        {
            Background = CreateBackgroundBrush(_settings?.OverlayOpacity ?? 0.86),
            BorderBrush = CreateBorderBrush(_settings?.OverlayOpacity ?? 0.86),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 3, 6, 4),
            Child = text
        };
    }

    private void FloatingPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isClickThrough && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void ApplyBackgroundOpacity(double opacity)
    {
        FloatingPanel.Background = CreateBackgroundBrush(opacity);
        FloatingPanel.BorderBrush = CreateBorderBrush(opacity);
    }

    private static SolidColorBrush CreateBackgroundBrush(double opacity)
    {
        byte alpha = (byte)(Math.Clamp(opacity, 0.0, 1.0) * 255);
        return new SolidColorBrush(MediaColor.FromArgb(alpha, 7, 9, 10));
    }

    private static SolidColorBrush CreateBorderBrush(double opacity)
    {
        byte alpha = (byte)(Math.Clamp(opacity, 0.0, 1.0) * 120);
        return new SolidColorBrush(MediaColor.FromArgb(alpha, 120, 217, 149));
    }

    private Rect ToDipRect(Rect deviceRect)
    {
        nint handle = new WindowInteropHelper(this).EnsureHandle();
        PresentationSource? source = PresentationSource.FromVisual(this) ?? HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget is null)
        {
            return deviceRect;
        }

        Matrix transform = source.CompositionTarget.TransformFromDevice;
        WpfPoint topLeft = transform.Transform(new WpfPoint(deviceRect.Left, deviceRect.Top));
        WpfPoint bottomRight = transform.Transform(new WpfPoint(deviceRect.Right, deviceRect.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private void ApplyClickThrough(bool enabled)
    {
        if (!IsLoaded)
        {
            return;
        }

        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            return;
        }

        int style = GetWindowLong(handle, GwlExstyle);
        if (enabled)
        {
            style |= WsExTransparent | WsExLayered;
        }
        else
        {
            style &= ~WsExTransparent;
            style |= WsExLayered;
        }

        SetWindowLong(handle, GwlExstyle, style);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
}
