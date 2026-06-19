using System.Windows;
using OwTranslateLite.Core;

namespace OwTranslateLite.Overlay;

public sealed class OverlayController
{
    private OverlayWindow? _window;
    private bool _isApplyingSettings;

    public event EventHandler? BoundsChangedByUser;
    public event EventHandler<ReplySubmittedEventArgs>? ReplySubmitted;
    public event EventHandler<string>? CopyReplyRequested;
    public event EventHandler<string>? ReplyTargetLanguageChanged;
    public event EventHandler? ReplyEditingStarted;
    public event EventHandler? ReplyModeExited;

    public bool IsCreated => _window is not null;
    public bool IsVisible => _window?.IsVisible == true;

    public void Ensure(AppSettings settings)
    {
        if (_window is null)
        {
            _window = new OverlayWindow();
            _window.LocationChanged += OverlayWindow_BoundsChanged;
            _window.SizeChanged += OverlayWindow_BoundsChanged;
            _window.ReplySubmitted += (_, args) => ReplySubmitted?.Invoke(this, args);
            _window.CopyReplyRequested += (_, text) => CopyReplyRequested?.Invoke(this, text);
            _window.ReplyEditingStarted += (_, args) => ReplyEditingStarted?.Invoke(this, args);
            _window.ReplyTargetLanguageChanged += (_, language) => ReplyTargetLanguageChanged?.Invoke(this, language);
            _window.ReplyModeExited += (_, args) => ReplyModeExited?.Invoke(this, args);
        }

        ApplySettings(settings);
    }

    public void ApplySettings(AppSettings settings)
    {
        if (_window is null)
        {
            return;
        }

        _isApplyingSettings = true;
        try
        {
            _window.ApplySettings(settings);
        }
        finally
        {
            _isApplyingSettings = false;
        }
    }

    public bool SaveBoundsTo(AppSettings settings)
    {
        if (_window is null ||
            !IsFinite(_window.Left) ||
            !IsFinite(_window.Top) ||
            !IsFinite(_window.Width) ||
            !IsFinite(_window.Height))
        {
            return false;
        }

        settings.OverlayLeft = _window.Left;
        settings.OverlayTop = _window.Top;
        settings.OverlayWidth = _window.Width;
        settings.OverlayHeight = _window.Height;
        return true;
    }

    public void MoveNear(Rect rect)
    {
        _window?.MoveNear(rect);
    }

    public void Show()
    {
        _window?.Show();
    }

    public void ShowAndActivate()
    {
        _window?.Show();
        _window?.Activate();
    }

    public void Hide()
    {
        _window?.Hide();
    }

    public void Close()
    {
        _window?.Close();
        _window = null;
    }

    public void UpdateRecords(IReadOnlyList<TranslationRecord> records)
    {
        _window?.UpdateRecords(records);
    }

    public void EnterReplyMode(string replyTargetLanguage, string effectiveLanguage)
    {
        _window?.EnterReplyMode(replyTargetLanguage, effectiveLanguage);
    }

    public void ExitReplyMode()
    {
        _window?.ExitReplyMode();
    }

    public void SetReplyTargetLanguage(string replyTargetLanguage, string effectiveLanguage)
    {
        _window?.SetReplyTargetLanguage(replyTargetLanguage, effectiveLanguage);
    }

    public void SetReplyStatus(string status)
    {
        _window?.SetReplyStatus(status);
    }

    public void ClearReplyInput()
    {
        _window?.ClearReplyInput();
    }

    public void SetReplyTranslation(string translated)
    {
        _window?.SetReplyTranslation(translated);
    }

    private void OverlayWindow_BoundsChanged(object? sender, EventArgs e)
    {
        if (!_isApplyingSettings)
        {
            BoundsChangedByUser?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}
