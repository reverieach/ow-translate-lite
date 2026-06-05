using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using OwTranslateLite.Core;
using MediaColor = System.Windows.Media.Color;

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
        ApplySavedBounds(settings);
        RenderRecords();
    }

    public void UpdateRecords(IReadOnlyList<TranslationRecord> records)
    {
        _records = records.ToList();
        RenderRecords();
    }

    public void MoveNear(Rect captureRegion)
    {
        if (_settings?.OverlayLeft is double left && _settings.OverlayTop is double top)
        {
            ApplySavedBounds(_settings);
            return;
        }

        Left = captureRegion.Left;
        Top = Math.Max(0, captureRegion.Top - Height - 12);
        Width = Math.Max(420, captureRegion.Width);
    }

    private void ApplySavedBounds(AppSettings settings)
    {
        if (settings.OverlayLeft is double left && settings.OverlayTop is double top)
        {
            Left = left;
            Top = top;
            Width = Math.Max(260, settings.OverlayWidth ?? Width);
            Height = Math.Max(100, settings.OverlayHeight ?? Height);
        }
    }

    private void RenderRecords()
    {
        RecordList.ItemsSource = _records.ToList();
        Dispatcher.BeginInvoke(() => TranslationScrollViewer.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);
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
