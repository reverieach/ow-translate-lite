using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace OwTranslateLite.Core;

public sealed class ClipboardService
{
    private const int RetryCount = 8;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(200);

    public async Task<ClipboardSetResult> SetTextWithRetryAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        Exception? lastException = null;
        for (int attempt = 1; attempt <= RetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                System.Windows.Clipboard.SetText(text);
                return ClipboardSetResult.Success(attempt);
            }
            catch (COMException ex)
            {
                lastException = ex;
            }
            catch (ExternalException ex)
            {
                lastException = ex;
            }
            catch (InvalidOperationException ex)
            {
                lastException = ex;
            }

            if (attempt < RetryCount)
            {
                int nextDelay = (int)Math.Min(MaxDelay.TotalMilliseconds, InitialDelay.TotalMilliseconds + attempt * 20);
                await Task.Delay(nextDelay, cancellationToken);
            }
        }

        return ClipboardSetResult.Failure(
            RetryCount,
            GetClipboardOwnerDescription(),
            lastException?.Message ?? "剪贴板被其他程序占用。");
    }

    private static string GetClipboardOwnerDescription()
    {
        nint hwnd = GetOpenClipboardWindow();
        if (hwnd == 0)
        {
            return "";
        }

        _ = GetWindowThreadProcessId(hwnd, out int processId);
        if (processId <= 0)
        {
            return $"窗口句柄 0x{hwnd.ToInt64():X}";
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            return $"{process.ProcessName}.exe";
        }
        catch
        {
            return $"PID {processId}";
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetOpenClipboardWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(nint hWnd, out int processId);
}

public sealed record ClipboardSetResult(
    bool Succeeded,
    int Attempts,
    string OwnerDescription,
    string ErrorMessage)
{
    public static ClipboardSetResult Success(int attempts) =>
        new(true, attempts, "", "");

    public static ClipboardSetResult Failure(int attempts, string ownerDescription, string errorMessage) =>
        new(false, attempts, ownerDescription, errorMessage);
}
