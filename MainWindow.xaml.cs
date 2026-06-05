using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using OwTranslateLite.Core;
using OwTranslateLite.Ocr;
using OwTranslateLite.Overlay;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace OwTranslateLite;

public partial class MainWindow : Window
{
    private readonly ConfigStore _config = new();
    private OwGlossaryService _glossary = null!;
    private TranslationCoordinator _coordinator = null!;
    private OverlayWindow? _overlay;
    private CancellationTokenSource? _loopCts;
    private IOcrEngine? _currentOcrEngine;
    private string? _currentOcrEngineName;
    private readonly List<TranslationRecord> _records = [];

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _config.Load();
        _glossary = OwGlossaryService.LoadDefault();
        _coordinator = new TranslationCoordinator(_config.Settings, _glossary);
        GlossaryStatusText.Text = $"术语 { _glossary.EntryCount } 项 · { _glossary.Version }";
        LoadSettingsToUi();
        EnsureOverlay();
        AddLog("就绪。建议先用 Local 模式和记事本文字做冒烟测试。");
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        StopLoop();
        SaveSettingsFromUi();
        _overlay?.Close();
    }

    private void LoadSettingsToUi()
    {
        AppSettings settings = _config.Settings;
        SelectCombo(OcrEngineCombo, settings.OcrEngine);
        SelectCombo(OcrLanguageCombo, settings.OcrLanguage);
        SelectCombo(ProviderCombo, settings.TranslationProvider);
        SelectCombo(OverlayModeCombo, settings.OverlayMode);
        ApiUrlBox.Text = settings.ApiUrl;
        ApiKeyBox.Password = settings.ApiKey;
        ModelBox.Text = settings.Model;
        FontSizeSlider.Value = settings.OverlayFontSize;
        OpacitySlider.Value = settings.OverlayOpacity;
        ClickThroughCheck.IsChecked = settings.OverlayClickThrough;
        FirstRunPanel.Visibility = settings.FirstRun ? Visibility.Visible : Visibility.Collapsed;
        UpdateProviderPreset();
        UpdateRegionText();
    }

    private void SaveSettingsFromUi()
    {
        AppSettings settings = _config.Settings;
        settings.OcrEngine = GetComboText(OcrEngineCombo);
        settings.OcrLanguage = GetComboText(OcrLanguageCombo);
        settings.TranslationProvider = GetComboText(ProviderCombo);
        settings.OverlayMode = GetComboText(OverlayModeCombo);
        settings.ApiUrl = ApiUrlBox.Text.Trim();
        settings.ApiKey = ApiKeyBox.Password.Trim();
        settings.Model = ModelBox.Text.Trim();
        settings.OverlayFontSize = FontSizeSlider.Value;
        settings.OverlayOpacity = OpacitySlider.Value;
        settings.OverlayClickThrough = ClickThroughCheck.IsChecked == true;
        SaveOverlayBounds(settings);
        _config.Save();
        _overlay?.ApplySettings(settings);
    }

    private void SelectCombo(WpfComboBox combo, string value)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static string GetComboText(WpfComboBox combo)
    {
        return (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
    }

    private void UpdateProviderPreset()
    {
        string provider = GetComboText(ProviderCombo);
        bool apiEnabled = provider != "Local";
        ApiUrlBox.IsEnabled = apiEnabled;
        ApiKeyBox.IsEnabled = apiEnabled;
        ModelBox.IsEnabled = apiEnabled;

        if (provider == "DeepSeek" && string.IsNullOrWhiteSpace(ApiUrlBox.Text))
        {
            ApiUrlBox.Text = "https://api.deepseek.com/v1/chat/completions";
            ModelBox.Text = "deepseek-chat";
        }
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        UpdateProviderPreset();
    }

    private void FinishFirstRun_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        _config.Settings.FirstRun = false;
        _config.Save();
        FirstRunPanel.Visibility = Visibility.Collapsed;
        AddLog("首次配置完成。");
    }

    private void SelectArea_Click(object sender, RoutedEventArgs e)
    {
        AreaSelectorWindow selector = new();
        selector.Owner = this;
        selector.SelectionCompleted += (_, rect) =>
        {
            _config.Settings.CaptureRegion = CaptureRegion.FromRect(rect);
            _config.Save();
            UpdateRegionText();
            EnsureOverlay();
            _overlay?.MoveNear(rect);
            AddLog($"已选择区域 {rect.Left:0},{rect.Top:0} {rect.Width:0}x{rect.Height:0}");
        };
        selector.ShowDialog();
    }

    private void ShowOverlay_Click(object sender, RoutedEventArgs e)
    {
        EnsureOverlay();
        _overlay?.Show();
        _overlay?.Activate();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        if (_config.Settings.CaptureRegion is null)
        {
            AddLog("请先选择聊天区域。");
            return;
        }

        EnsureOverlay();
        _overlay?.Show();
        StopLoop();
        _loopCts = new CancellationTokenSource();
        StatusText.Text = "运行中";
        _ = RunLoopAsync(_loopCts.Token);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopLoop();
        StatusText.Text = "已暂停";
        AddLog("已暂停。");
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        AddLog("设置已保存。");
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogList.Items.Clear();
        _records.Clear();
            _overlay?.UpdateRecords(_records);
    }

    private void SaveOverlayBounds(AppSettings settings)
    {
        if (_overlay is null || settings.OverlayMode != "Floating")
        {
            return;
        }

        settings.OverlayLeft = _overlay.Left;
        settings.OverlayTop = _overlay.Top;
        settings.OverlayWidth = _overlay.Width;
        settings.OverlayHeight = _overlay.Height;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                IOcrEngine engine = GetOcrEngine();
                IReadOnlyList<TranslationRecord> records = await _coordinator.ProcessAsync(engine, cancellationToken);
                if (records.Count > 0)
                {
                    Dispatcher.Invoke(() =>
                    {
                        foreach (TranslationRecord record in records)
                        {
                            _records.Add(record);
                            AddLog($"{record.Speaker}: {record.SourceText}  =>  {record.TranslatedText}");
                        }

                        while (_records.Count > 8)
                        {
                            _records.RemoveAt(0);
                        }

                        _overlay?.UpdateRecords(_records);
                    });
                }

                stopwatch.Stop();
                Dispatcher.Invoke(() => LatencyText.Text = $"{stopwatch.ElapsedMilliseconds} ms");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AddLog($"错误：{ex.Message}"));
            }

            int delay = Math.Clamp(_config.Settings.CaptureIntervalMs, 400, 3000);
            await Task.Delay(delay, cancellationToken);
        }
    }

    private IOcrEngine GetOcrEngine()
    {
        if (_currentOcrEngine is not null && _currentOcrEngineName == _config.Settings.OcrEngine)
        {
            return _currentOcrEngine;
        }

        _currentOcrEngineName = _config.Settings.OcrEngine;
        _currentOcrEngine = _config.Settings.OcrEngine == "Windows OCR"
            ? new WindowsOcrEngine()
            : new OneOcrEngine();

        return _currentOcrEngine;
    }

    private void StopLoop()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = null;
    }

    private void EnsureOverlay()
    {
        if (_overlay is not null)
        {
            _overlay.ApplySettings(_config.Settings);
            return;
        }

        _overlay = new OverlayWindow();
        _overlay.ApplySettings(_config.Settings);
        if (_config.Settings.CaptureRegion is CaptureRegion region)
        {
            _overlay.MoveNear(region.ToRect());
        }
    }

    private void UpdateRegionText()
    {
        RegionText.Text = _config.Settings.CaptureRegion is CaptureRegion region
            ? $"区域：{region.Left:0},{region.Top:0}  {region.Width:0}x{region.Height:0}"
            : "未选择区域";
    }

    private void AddLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogList.Items.Add(line);
        LogList.ScrollIntoView(line);
    }
}
