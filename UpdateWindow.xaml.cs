using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using OwTranslateLite.Core;

namespace OwTranslateLite;

public partial class UpdateWindow : Window
{
    private readonly UpdateCheckResult _update;

    public UpdateWindow(UpdateCheckResult update)
    {
        InitializeComponent();
        _update = update;
        SelectedAction = UpdateWindowAction.RemindLater;
        Populate();
    }

    public UpdateWindowAction SelectedAction { get; private set; }

    public bool IgnoreVersion => IgnoreVersionCheck.IsChecked == true;

    private void Populate()
    {
        VersionText.Text = $"{_update.CurrentVersion} -> {_update.LatestVersion}";
        MetaText.Text = _update.PublishedAt is DateTime publishedAt
            ? $"发布时间：{publishedAt:yyyy-MM-dd HH:mm}"
            : "发布时间：未知";
        AssetText.Text = _update.PackageAsset is UpdateAsset asset
            ? $"安装包：{asset.Name}  ({FormatSize(asset.SizeBytes)})"
            : "没有找到可自动更新的 win-x64 便携包，可打开发布页手动下载。";
        ReleaseNotesText.Text = LimitReleaseNotes(_update.ReleaseNotes);
    }

    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = UpdateWindowAction.UpdateNow;
        DialogResult = true;
    }

    private void OpenReleasePage_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = UpdateWindowAction.OpenReleasePage;
        DialogResult = true;
    }

    private void RemindLater_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = UpdateWindowAction.RemindLater;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = UpdateWindowAction.RemindLater;
        DialogResult = true;
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
            // Window drag is convenience-only.
        }
    }

    private static string LimitReleaseNotes(string value)
    {
        string trimmed = string.IsNullOrWhiteSpace(value)
            ? "此版本没有填写更新说明。"
            : value.Trim();
        return trimmed.Length <= 1400 ? trimmed : trimmed[..1400] + "\n\n……完整更新说明请打开发布页查看。";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "未知大小";
        }

        double mb = bytes / 1024d / 1024d;
        return $"{mb:0.0} MB";
    }

    public static void OpenReleasePage(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
}

public enum UpdateWindowAction
{
    RemindLater,
    OpenReleasePage,
    UpdateNow
}
