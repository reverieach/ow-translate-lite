using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using OwTranslateLite.Core;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using MediaColor = System.Windows.Media.Color;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;

namespace OwTranslateLite.Overlay;

public partial class OverlayWindow : Window
{
    private const double MinOverlayWidth = 260;
    private const double MinOverlayHeight = 100;
    private const double MinVisiblePixels = 80;
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;

    private AppSettings? _settings;
    private IReadOnlyList<TranslationRecord> _records = [];
    private bool _isClickThrough = true;
    private bool _isReplyMode;
    private string _replyTargetLanguage = "auto";
    private string _effectiveReplyLanguage = "en";
    private bool _isDragging;
    private WpfPoint _dragStartMouse;
    private double _dragStartLeft;
    private double _dragStartTop;

    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyClickThrough(_isClickThrough);
        DragHandle.MouseLeftButtonDown += DragHandle_MouseLeftButtonDown;
        DragHandle.MouseMove += DragHandle_MouseMove;
        DragHandle.MouseLeftButtonUp += DragHandle_MouseLeftButtonUp;
        ResizeGrip.DragDelta += ResizeGrip_DragDelta;
        ReplyInputBox.PreviewKeyDown += ReplyInputBox_PreviewKeyDown;
        ReplyTargetCombo.SelectionChanged += ReplyTargetCombo_SelectionChanged;
    }

    public event EventHandler<ReplySubmittedEventArgs>? ReplySubmitted;
    public event EventHandler<string>? ReplyTargetLanguageChanged;
    public event EventHandler? ReplyModeExited;

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _isClickThrough = settings.OverlayClickThrough;
        RecordList.FontSize = settings.OverlayFontSize;
        ReplyInputBox.FontSize = settings.OverlayFontSize;
        ReplyStatusText.FontSize = Math.Max(11, settings.OverlayFontSize - 2);
        ReplyTargetCombo.FontSize = Math.Max(11, settings.OverlayFontSize - 2);
        ApplyBackgroundOpacity(settings.OverlayOpacity);
        ApplyClickThrough(_isReplyMode ? false : settings.OverlayClickThrough);
        ApplySavedBounds(settings);
        RenderRecords();
    }

    public void EnterReplyMode(string replyTargetLanguage, string effectiveLanguage)
    {
        _isReplyMode = true;
        SetReplyTargetLanguage(replyTargetLanguage, effectiveLanguage);
        ReplyInputPanel.Visibility = Visibility.Visible;
        SetReplyStatus("输入中文回车复制");
        ApplyClickThrough(false);
        Show();
        Activate();
        Dispatcher.BeginInvoke(() =>
        {
            ReplyInputBox.Focus();
            Keyboard.Focus(ReplyInputBox);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    public void ExitReplyMode()
    {
        _isReplyMode = false;
        ReplyInputBox.Clear();
        ReplyInputPanel.Visibility = Visibility.Collapsed;
        ApplyClickThrough(_settings?.OverlayClickThrough == true);
    }

    public void SetReplyTargetLanguage(string replyTargetLanguage, string effectiveLanguage)
    {
        _replyTargetLanguage = NormalizeReplyLanguage(replyTargetLanguage);
        _effectiveReplyLanguage = NormalizeReplyLanguage(effectiveLanguage) == "auto" ? "en" : NormalizeReplyLanguage(effectiveLanguage);
        foreach (object item in ReplyTargetCombo.Items)
        {
            if (item is ComboBoxItem comboItem &&
                string.Equals(comboItem.Tag?.ToString(), _replyTargetLanguage, StringComparison.OrdinalIgnoreCase))
            {
                ReplyTargetCombo.SelectedItem = comboItem;
                break;
            }
        }

        UpdateReplyTargetComboText();
    }

    public void SetReplyStatus(string status)
    {
        ReplyStatusText.Text = status;
    }

    public void ClearReplyInput()
    {
        ReplyInputBox.Clear();
        ReplyInputBox.Focus();
        Keyboard.Focus(ReplyInputBox);
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
        KeepMostlyOnScreen();
    }

    private void ApplySavedBounds(AppSettings settings)
    {
        if (settings.OverlayLeft is double left &&
            settings.OverlayTop is double top &&
            IsFinite(left) &&
            IsFinite(top))
        {
            Left = left;
            Top = top;
            if (settings.OverlayWidth is double width && IsFinite(width))
            {
                Width = Math.Max(MinOverlayWidth, width);
            }

            if (settings.OverlayHeight is double height && IsFinite(height))
            {
                Height = Math.Max(MinOverlayHeight, height);
            }

            KeepMostlyOnScreen();
        }
    }

    private void RenderRecords()
    {
        RecordList.ItemsSource = _records.ToList();
        Dispatcher.BeginInvoke(() => TranslationScrollViewer.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isClickThrough || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        _isDragging = true;
        _dragStartMouse = GetCursorPositionDip();
        _dragStartLeft = Left;
        _dragStartTop = Top;
        DragHandle.CaptureMouse();
        e.Handled = true;
    }

    private void DragHandle_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_isDragging || _isClickThrough || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        WpfPoint current = GetCursorPositionDip();
        Left = _dragStartLeft + current.X - _dragStartMouse.X;
        Top = _dragStartTop + current.Y - _dragStartMouse.Y;
        KeepMostlyOnScreen();
        e.Handled = true;
    }

    private void DragHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        DragHandle.ReleaseMouseCapture();
    }

    private void ResizeGrip_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (_isClickThrough)
        {
            return;
        }

        Width = Math.Max(MinOverlayWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinOverlayHeight, Height + e.VerticalChange);
        KeepMostlyOnScreen();
    }

    private void ApplyBackgroundOpacity(double opacity)
    {
        FloatingPanel.Background = CreateBackgroundBrush(opacity);
        FloatingPanel.BorderBrush = CreateBorderBrush(opacity);
        ReplyInputPanel.Background = CreateInputBackgroundBrush(opacity);
        ReplyInputBox.Background = CreateInputBoxBrush(opacity);
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

    private static SolidColorBrush CreateInputBackgroundBrush(double opacity)
    {
        byte alpha = (byte)(Math.Clamp(opacity + 0.28, 0.35, 0.92) * 255);
        return new SolidColorBrush(MediaColor.FromArgb(alpha, 10, 13, 15));
    }

    private static SolidColorBrush CreateInputBoxBrush(double opacity)
    {
        byte alpha = (byte)(Math.Clamp(opacity + 0.2, 0.28, 0.88) * 255);
        return new SolidColorBrush(MediaColor.FromArgb(alpha, 7, 9, 10));
    }

    private void ApplyClickThrough(bool enabled)
    {
        if (_isReplyMode)
        {
            enabled = false;
        }

        DragHandleRow.Height = enabled ? new GridLength(0) : new GridLength(20);
        DragHandle.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ResizeGrip.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        DragHandle.IsHitTestVisible = !enabled;
        ResizeGrip.IsHitTestVisible = !enabled;

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

    private void ReplyInputBox_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ReplyModeExited?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        string text = ReplyInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            SetReplyStatus("请输入中文");
            return;
        }

        ReplySubmitted?.Invoke(this, new ReplySubmittedEventArgs(text, _replyTargetLanguage, _effectiveReplyLanguage));
    }

    private void ReplyTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReplyTargetCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        string language = NormalizeReplyLanguage(item.Tag?.ToString() ?? "auto");
        _replyTargetLanguage = language;
        UpdateReplyTargetComboText();
        ReplyTargetLanguageChanged?.Invoke(this, language);
    }

    private void UpdateReplyTargetComboText()
    {
        if (ReplyTargetCombo.SelectedItem is ComboBoxItem selected)
        {
            selected.Content = _replyTargetLanguage == "auto"
                ? $"自动->{GetLanguageLabel(_effectiveReplyLanguage)}"
                : GetLanguageLabel(_replyTargetLanguage);
        }

        foreach (object item in ReplyTargetCombo.Items)
        {
            if (item is not ComboBoxItem comboItem)
            {
                continue;
            }

            string tag = NormalizeReplyLanguage(comboItem.Tag?.ToString() ?? "auto");
            if (tag != _replyTargetLanguage)
            {
                comboItem.Content = tag == "auto" ? "自动" : GetLanguageLabel(tag);
            }
        }
    }

    private static string NormalizeReplyLanguage(string language)
    {
        return language is "en" or "ja" or "ko" ? language : "auto";
    }

    private static string GetLanguageLabel(string language)
    {
        return language switch
        {
            "ja" => "日语",
            "ko" => "韩语",
            _ => "英语"
        };
    }

    private void KeepMostlyOnScreen()
    {
        double minLeft = SystemParameters.VirtualScreenLeft - ActualWidth + MinVisiblePixels;
        double maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - MinVisiblePixels;
        double minTop = SystemParameters.VirtualScreenTop;
        double maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - MinVisiblePixels;

        if (IsFinite(Left))
        {
            Left = Math.Clamp(Left, minLeft, maxLeft);
        }

        if (IsFinite(Top))
        {
            Top = Math.Clamp(Top, minTop, maxTop);
        }
    }

    private WpfPoint GetCursorPositionDip()
    {
        if (!GetCursorPos(out NativePoint nativePoint))
        {
            return PointToScreen(Mouse.GetPosition(this));
        }

        WpfPoint point = new(nativePoint.X, nativePoint.Y);
        PresentationSource? source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget is null
            ? point
            : source.CompositionTarget.TransformFromDevice.Transform(point);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}

public sealed class ReplySubmittedEventArgs : EventArgs
{
    public ReplySubmittedEventArgs(string sourceText, string selectedLanguage, string effectiveLanguage)
    {
        SourceText = sourceText;
        SelectedLanguage = selectedLanguage;
        EffectiveLanguage = effectiveLanguage;
    }

    public string SourceText { get; }
    public string SelectedLanguage { get; }
    public string EffectiveLanguage { get; }
}
