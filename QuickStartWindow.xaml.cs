using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OwTranslateLite;

public partial class QuickStartWindow : Window
{
    private const double WheelScrollPixelsPerNotch = 240;

    public QuickStartWindow()
    {
        InitializeComponent();
    }

    public bool DoNotShowAgain => DoNotShowAgainCheck.IsChecked == true;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove can throw if the mouse button state changes mid-call.
        }
    }

    private void GuideScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer)
        {
            return;
        }

        double deltaNotches = e.Delta / 120.0;
        double target = Math.Clamp(
            viewer.VerticalOffset - deltaNotches * WheelScrollPixelsPerNotch,
            0,
            viewer.ScrollableHeight);
        viewer.ScrollToVerticalOffset(target);
        e.Handled = true;
    }

    private void GuideImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Image image || image.Source is null)
        {
            return;
        }

        string source = image.Source.ToString() ?? "";
        string path = Uri.TryCreate(source, UriKind.Absolute, out Uri? absoluteUri)
            ? absoluteUri.LocalPath
            : Path.Combine(AppContext.BaseDirectory, source.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
        e.Handled = true;
    }
}
