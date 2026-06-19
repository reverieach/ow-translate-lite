using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using OwTranslateLite.Core;
using OwTranslateLite.Ocr;
using OwTranslateLite.Overlay;
using OwTranslateLite.Translation;
using WpfTextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace OwTranslateLite;

public partial class MainWindow : Window
{
    private static readonly TimeSpan OverlayIdleHideDelay = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan TextGateHeartbeatInterval = TimeSpan.FromSeconds(5);
    private const int MinSamplingIntervalMs = 250;
    private const int MaxSamplingIntervalMs = 300;
    private const int BurstOcrFrameCount = 3;
    private const int MaxOverlayRecords = 50;
    private const int MaxLogRecords = 200;
    private const int TranslationQueueSoftBatchThreshold = 30;
    private const int TranslationQueueHardLimit = 100;
    private const int MaxTranslationBatchSize = 4;
    private const int MaxOverflowTranslationBatchSize = 30;
    private const int MaxTranslationRetries = 2;
    private const int ReplyHotkeyId = 0x4F57;
    private const string ProjectHomeUrl = "https://github.com/reverieach/ow-translate-lite";
    private const string ProjectIssuesUrl = "https://github.com/reverieach/ow-translate-lite/issues/new";
    private const string ContactEmail = "reverie-ach@outlook.com";

    private readonly ConfigStore _config = new();
    private readonly RecentChatLanguageTracker _recentChatLanguages = new();
    private readonly HotKeyService _replyHotKey = new(ReplyHotkeyId);
    private readonly DiagnosticsService _diagnostics = new();
    private readonly UpdateService _updateService = new();
    private readonly OverlayController _overlayController = new();
    private OwGlossaryService _glossary = null!;
    private TranslationCoordinator _coordinator = null!;
    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource? _replyTranslationCts;
    private CancellationTokenSource? _fetchModelsCts;
    private CancellationTokenSource? _updateCheckCts;
    private readonly OcrEngineManager _ocrEngineManager = new();
    private readonly FrameDiffGate _frameDiffGate = new();
    private readonly TextPresenceGate _textPresenceGate = new();
    private readonly ClipboardService _clipboardService = new();
    private readonly Queue<ParsedChatLine> _translationQueue = [];
    private readonly object _translationQueueLock = new();
    private readonly TranslationQueueStatusTracker _translationQueueStatus = new();
    private Task? _translationWorkerTask;
    private string? _activeRunSettingsKey;
    private DateTime? _pausedAt;
    private DateTime? _lastTranslationCompletedAt;
    private bool _overlayHiddenByIdle;
    private bool _isRunning;
    private bool _isReplyModeActive;
    private bool _isLoadingSettings;
    private bool _settingsLoaded;
    private bool _isAdjustingTranslationFrame;
    private DateTime _lastTextGateOcrAt = DateTime.Now;
    private int _consecutiveTextGateRejects;
    private int _burstOcrFramesRemaining;
    private int _consecutiveNoChatFrames;
    private int _runGeneration;
    private QuickStartWindow? _quickStartWindow;
    private readonly List<TranslationRecord> _records = [];

    public MainWindow()
    {
        InitializeComponent();
        ModelCombo.AddHandler(WpfTextBoxBase.TextChangedEvent, new TextChangedEventHandler(TranslationSettings_Changed));
        _replyHotKey.Pressed += (_, _) => ToggleReplyMode();
        _overlayController.BoundsChangedByUser += Overlay_BoundsChanged;
        _overlayController.ReplySubmitted += Overlay_ReplySubmitted;
        _overlayController.CopyReplyRequested += Overlay_CopyReplyRequested;
        _overlayController.ReplyEditingStarted += Overlay_ReplyEditingStarted;
        _overlayController.ReplyTargetLanguageChanged += Overlay_ReplyTargetLanguageChanged;
        _overlayController.ReplyModeExited += Overlay_ReplyModeExited;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _config.Load();
        ThemeService.Apply(_config.Settings.ThemeMode);
        _glossary = OwGlossaryService.LoadDefault();
        _coordinator = CreateCoordinator();
        LoadSettingsToUi();
        _settingsLoaded = true;
        EnsureOverlay();
        ApplyOverlayVisibilityPreference(activate: false);
        ApplyRunningState();
        ApplyFrameAdjustmentState();
        AddLog("就绪。正式测试建议使用 DeepSeek API。");
        ShowInstallPathWarningIfNeeded();
        ApplyReplyHotkeyRegistration();
        ShowQuickStartIfNeeded();
        _ = CheckForUpdatesAsync(manual: false);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        EndFrameAdjustment(log: false);
        ExitReplyMode();
        _replyHotKey.Dispose();
        _replyTranslationCts?.Cancel();
        _fetchModelsCts?.Cancel();
        _updateCheckCts?.Cancel();
        StopLoop(hideOverlay: false, clearOverlay: false);
        _ocrEngineManager.Dispose();
        SaveSettingsFromUi();
        _quickStartWindow?.Close();
        _overlayController.Close();
    }

    private void LoadSettingsToUi()
    {
        _isLoadingSettings = true;
        try
        {
            AppSettings settings = _config.Settings;
            NormalizeOcrSettings(settings);
            NormalizeReplySettings(settings);
            SelectCombo(ProviderCombo, settings.TranslationProvider);
            EnsureDefaultModelOptions();
            ApiUrlBox.Text = settings.ApiUrl;
            ApiKeyBox.Password = settings.ApiKey;
            ModelCombo.Text = settings.Model;
            FontSizeSlider.Value = settings.OverlayFontSize;
            OpacitySlider.Value = settings.OverlayOpacity;
            ClickThroughCheck.IsChecked = settings.OverlayClickThrough;
            KeepOverlayVisibleCheck.IsChecked = settings.KeepOverlayVisible;
            ReplyInputBarCheck.IsChecked = settings.ShowReplyInputBar;
            AutoCopyReplyCheck.IsChecked = settings.AutoCopyReplyTranslation;
            ReplyHotkeyCheck.IsChecked = settings.EnableReplyHotkey;
            SelectCombo(ReplyHotkeyCombo, settings.ReplyHotkey);
            SelectCombo(ThemeModeCombo, settings.ThemeMode);
            DebugDiagnosticsCheck.IsChecked = settings.EnableDebugDiagnostics;
            FirstRunPanel.Visibility = settings.FirstRun ? Visibility.Visible : Visibility.Collapsed;
            UpdateProviderPreset();
            UpdateRegionText();
            RefreshRuntimeMetrics();
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void SaveSettingsFromUi()
    {
        if (!_settingsLoaded)
        {
            return;
        }

        AppSettings settings = _config.Settings;
        settings.OcrEngine = "OneOCR";
        settings.OcrLanguage = "auto";
        settings.TranslationProvider = GetComboText(ProviderCombo);
        settings.ApiUrl = ApiUrlBox.Text.Trim();
        settings.ApiKey = ApiKeyBox.Password.Trim();
        settings.Model = ModelCombo.Text.Trim();
        settings.OverlayFontSize = FontSizeSlider.Value;
        settings.OverlayOpacity = OpacitySlider.Value;
        settings.OverlayClickThrough = ClickThroughCheck.IsChecked == true;
        settings.KeepOverlayVisible = KeepOverlayVisibleCheck.IsChecked == true;
        settings.ShowReplyInputBar = ReplyInputBarCheck.IsChecked == true;
        settings.AutoCopyReplyTranslation = AutoCopyReplyCheck.IsChecked == true;
        settings.EnableReplyHotkey = ReplyHotkeyCheck.IsChecked == true;
        settings.ReplyHotkey = GetComboText(ReplyHotkeyCombo);
        settings.ThemeMode = ThemeService.Normalize(GetComboText(ThemeModeCombo));
        settings.EnableDebugDiagnostics = DebugDiagnosticsCheck.IsChecked == true;
        SaveOverlayBounds(settings);
        _config.Save();
        ApplyOverlaySettings();
        RefreshRuntimeMetrics();
    }

    private TranslationCoordinator CreateCoordinator() =>
        new(_config.Settings, _glossary, AppendDedupeLog);

    private void SelectCombo(WpfComboBox combo, string value)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            string itemValue = item.Tag?.ToString() ?? item.Content?.ToString() ?? "";
            if (string.Equals(itemValue, value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static string GetComboText(WpfComboBox combo)
    {
        if (combo.SelectedItem is not ComboBoxItem item)
        {
            return "";
        }

        return item.Tag?.ToString() ?? item.Content?.ToString() ?? "";
    }

    private void UpdateProviderPreset()
    {
        string provider = GetComboText(ProviderCombo);
        bool apiEnabled = true;
        ApiUrlBox.IsEnabled = apiEnabled;
        ApiKeyBox.IsEnabled = apiEnabled;
        ModelCombo.IsEnabled = apiEnabled;
        FetchModelsButton.IsEnabled = apiEnabled;

        if (provider == "DeepSeek")
        {
            EnsureDefaultModelOptions();
            if (string.IsNullOrWhiteSpace(ApiUrlBox.Text) ||
                string.Equals(ApiUrlBox.Text.Trim(), "https://api.deepseek.com/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                ApiUrlBox.Text = "https://api.deepseek.com";
            }

            string model = ModelCombo.Text.Trim();
            if (string.IsNullOrWhiteSpace(model) ||
                string.Equals(model, "deepseek-chat", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(model, "deepseek-reasoner", StringComparison.OrdinalIgnoreCase))
            {
                ModelCombo.Text = "deepseek-v4-flash";
            }
        }
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isLoadingSettings)
        {
            return;
        }

        UpdateProviderPreset();
        SaveSettingsFromUi();
        if (_isRunning)
        {
            RestartLoop(resetChatCycle: false, resetOcrEngine: false, "翻译设置已更新，已继续运行。");
        }
    }

    private void TranslationSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isLoadingSettings)
        {
            return;
        }

        SaveSettingsFromUi();
    }

    private void OverlaySettings_Changed(object sender, RoutedEventArgs e)
    {
        AutoSaveSettings();
    }

    private void DiagnosticsSettings_Changed(object sender, RoutedEventArgs e)
    {
        AutoSaveSettings();
    }

    private void ThemeMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isLoadingSettings)
        {
            return;
        }

        SaveSettingsFromUi();
        ThemeService.Apply(_config.Settings.ThemeMode);
        AddLog(_config.Settings.ThemeMode == ThemeService.Light
            ? "已切换为浅色主题。"
            : "已切换为深色主题。");
    }

    private void ReplyHotkeySettings_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isLoadingSettings)
        {
            return;
        }

        SaveSettingsFromUi();
        ApplyReplyHotkeyRegistration();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

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
            // DragMove can throw if the mouse button state changes during the call.
        }
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreWindow_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximizeRestore();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void FastScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer)
        {
            return;
        }

        double target = Math.Clamp(
            viewer.VerticalOffset - Math.Sign(e.Delta) * 260,
            0,
            viewer.ScrollableHeight);
        viewer.ScrollToVerticalOffset(target);
        e.Handled = true;
    }

    private void OverlaySettings_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        AutoSaveSettings();
    }

    private void AutoSaveSettings()
    {
        if (!IsLoaded || _isLoadingSettings)
        {
            return;
        }

        SaveSettingsFromUi();
    }

    private void FinishFirstRun_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        _config.Settings.FirstRun = false;
        _config.Save();
        FirstRunPanel.Visibility = Visibility.Collapsed;
        AddLog("首次配置完成。");
    }

    private void ShowInstallPathWarningIfNeeded()
    {
        string baseDirectory = AppContext.BaseDirectory;
        if (!ContainsCjk(baseDirectory))
        {
            return;
        }

        string message = "当前程序路径包含中文字符，少数机器上可能导致 OCR 或 native 组件加载失败。\n\n" +
                         "程序会继续启动；如果后续出现无法识别、OCR 初始化失败或启动异常，请把整个 OWTranslatorLite 文件夹移动到英文路径后再试，例如：\n" +
                         "C:\\OWTranslatorLite\\\n" +
                         "D:\\Tools\\OWTranslatorLite\\";
        AddLog("当前程序路径包含中文字符；如果 OCR 或启动异常，建议移动到英文路径后再试。");
        System.Windows.MessageBox.Show(message, "OW Translator Lite", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ShowQuickStartIfNeeded()
    {
        if (!_config.Settings.ShowQuickStart)
        {
            return;
        }

        ShowQuickStartWindow();
    }

    private void OpenQuickStart_Click(object sender, RoutedEventArgs e)
    {
        ShowQuickStartWindow();
    }

    private void ShowQuickStartWindow()
    {
        if (_quickStartWindow is { IsVisible: true })
        {
            _quickStartWindow.Activate();
            return;
        }

        QuickStartWindow quickStart = new()
        {
            Owner = this
        };
        _quickStartWindow = quickStart;
        quickStart.Closed += (_, _) =>
        {
            if (quickStart.DoNotShowAgain)
            {
                _config.Settings.ShowQuickStart = false;
                _config.Save();
            }

            if (ReferenceEquals(_quickStartWindow, quickStart))
            {
                _quickStartWindow = null;
            }
        };
        quickStart.Show();
        quickStart.Activate();
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
            _overlayController.MoveNear(rect);
            AddLog($"已选择区域 {rect.Left:0},{rect.Top:0} {rect.Width:0}x{rect.Height:0}");
        };
        selector.ShowDialog();
    }

    private void KeepOverlayVisible_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isLoadingSettings)
        {
            return;
        }

        SaveSettingsFromUi();
        ApplyOverlayVisibilityPreference(activate: true);
        AddLog(_config.Settings.KeepOverlayVisible
            ? "已切换为常态显示翻译框。"
            : "已切换为默认隐藏翻译框。");
    }

    private void AdjustFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_isAdjustingTranslationFrame)
        {
            EndFrameAdjustment(log: true);
            return;
        }

        BeginFrameAdjustment();
    }

    private void BeginFrameAdjustment()
    {
        EnsureOverlay();
        _isAdjustingTranslationFrame = true;
        ClickThroughCheck.IsChecked = false;
        SaveSettingsFromUi();
        ApplyFrameAdjustmentState();
        _overlayController.ShowAndActivate();
        AddLog("正在调整翻译框。拖动顶部横条移动，拖动右下角缩放；完成后点击“完成调整”恢复鼠标穿透。");
    }

    private void EndFrameAdjustment(bool log)
    {
        if (!_isAdjustingTranslationFrame)
        {
            return;
        }

        _isAdjustingTranslationFrame = false;
        ClickThroughCheck.IsChecked = true;
        SaveSettingsFromUi();
        ApplyFrameAdjustmentState();
        if (log)
        {
            AddLog("已完成翻译框调整，鼠标穿透已恢复。");
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        EndFrameAdjustment(log: true);
        SaveSettingsFromUi();
        if (_config.Settings.CaptureRegion is null)
        {
            AddLog("请先选择聊天区域。");
            return;
        }

        EnsureOverlay();
        string settingsKey = CreateRunSettingsKey();
        bool settingsChanged = !string.Equals(_activeRunSettingsKey, settingsKey, StringComparison.Ordinal);
        bool pausedLongEnoughToReset = _pausedAt is DateTime pausedAt && DateTime.Now - pausedAt >= TimeSpan.FromSeconds(3);
        bool resetChatCycle = settingsChanged || pausedLongEnoughToReset;
        RestartLoop(resetChatCycle, settingsChanged, resetChatCycle ? "已开始新的识别会话。" : "已继续运行。");
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopLoop(hideOverlay: true, clearOverlay: false);
        StatusText.Text = "已暂停";
        AddLog("已暂停。");
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        if (string.IsNullOrWhiteSpace(_config.Settings.ApiUrl))
        {
            AddLog("请先填写 API URL。");
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.Settings.ApiKey))
        {
            AddLog("请先填写 API Key。");
            return;
        }

        FetchModelsButton.IsEnabled = false;
        _fetchModelsCts?.Cancel();
        _fetchModelsCts?.Dispose();
        _fetchModelsCts = new CancellationTokenSource();
        CancellationTokenSource fetchCts = _fetchModelsCts;
        try
        {
            AddLog("正在获取模型列表...");
            IReadOnlyList<string> models = await OpenAICompatibleTranslationProvider.FetchModelIdsAsync(
                _config.Settings,
                fetchCts.Token);

            if (models.Count == 0)
            {
                AddLog("没有从 API 返回可用模型。");
                return;
            }

            string current = ModelCombo.Text.Trim();
            ModelCombo.Items.Clear();
            foreach (string model in models)
            {
                AddModelOption(model);
            }

            ModelCombo.Text = models.Contains(current, StringComparer.OrdinalIgnoreCase)
                ? current
                : models[0];
            SaveSettingsFromUi();
            AddLog($"已获取 {models.Count} 个模型。");
            RefreshRuntimeMetrics();
        }
        catch (OperationCanceledException) when (fetchCts.IsCancellationRequested)
        {
            AddLog("获取模型已取消。");
        }
        catch (Exception ex)
        {
            AddLog($"获取模型失败：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_fetchModelsCts, fetchCts))
            {
                _fetchModelsCts.Dispose();
                _fetchModelsCts = null;
            }

            FetchModelsButton.IsEnabled = true;
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogList.Items.Clear();
        ClearOverlayRecords();
    }

    private void OpenDataDirectory_Click(object sender, RoutedEventArgs e)
    {
        _diagnostics.OpenAppDirectory();
        AddLog($"已打开数据目录：{ConfigStore.AppDirectory}");
    }

    private void OpenLogsDirectory_Click(object sender, RoutedEventArgs e)
    {
        _diagnostics.OpenLogsDirectory();
        AddLog($"已打开日志文件夹：{ConfigStore.LogsDirectory}");
    }

    private void ExportFeedbackPackage_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        string diagnosticsPath = _diagnostics.ExportFeedbackPackage(
            _config.Settings,
            LogList.Items.Cast<object>().Select(static item => item.ToString() ?? ""),
            CreateRuntimeDiagnosticsSnapshot());
        AddLog($"已导出反馈包：{diagnosticsPath}");
    }

    private void ReportBug_Click(object sender, RoutedEventArgs e)
    {
        string title = Uri.EscapeDataString("[Bug] 请简要描述问题");
        string body = Uri.EscapeDataString(CreateIssueTemplate());
        OpenShellPath($"{ProjectIssuesUrl}?title={title}&body={body}");
        AddLog("已打开 GitHub Bug 反馈页面。");
    }

    private void OpenProjectHome_Click(object sender, RoutedEventArgs e)
    {
        OpenShellPath(ProjectHomeUrl);
        AddLog("已打开项目主页。");
    }

    private void CopyContactEmail_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(ContactEmail);
        AddLog($"已复制联系邮箱：{ContactEmail}");
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(manual: true);
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        _updateCheckCts?.Cancel();
        _updateCheckCts?.Dispose();
        _updateCheckCts = new CancellationTokenSource();
        CancellationTokenSource checkCts = _updateCheckCts;
        try
        {
            if (manual)
            {
                AddLog("正在检查更新...");
            }

            UpdateCheckResult update = await _updateService.CheckLatestAsync(checkCts.Token);
            if (!update.IsNewer)
            {
                if (manual)
                {
                    AddLog($"当前已是最新版本：{update.CurrentVersion}");
                    System.Windows.MessageBox.Show(
                        $"当前已是最新版本：{update.CurrentVersion}",
                        "检查更新",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;
            }

            if (!manual &&
                string.Equals(_config.Settings.IgnoredUpdateVersion, update.LatestVersion, StringComparison.OrdinalIgnoreCase))
            {
                AddLog($"已忽略更新版本：{update.LatestVersion}");
                return;
            }

            ShowUpdatePrompt(update);
        }
        catch (OperationCanceledException) when (checkCts.IsCancellationRequested)
        {
            // A newer update check superseded this one.
        }
        catch (Exception ex)
        {
            if (manual)
            {
                System.Windows.MessageBox.Show(
                    $"检查更新失败：{ex.Message}",
                    "检查更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                AddLog($"检查更新失败：{ex.Message}");
            }
        }
        finally
        {
            if (ReferenceEquals(_updateCheckCts, checkCts))
            {
                _updateCheckCts.Dispose();
                _updateCheckCts = null;
            }
        }
    }

    private void ShowUpdatePrompt(UpdateCheckResult update)
    {
        UpdateWindow window = new(update)
        {
            Owner = this
        };
        _ = window.ShowDialog();
        if (window.IgnoreVersion)
        {
            _config.Settings.IgnoredUpdateVersion = update.LatestVersion;
            _config.Save();
            AddLog($"已设置不再提醒版本：{update.LatestVersion}");
        }

        switch (window.SelectedAction)
        {
            case UpdateWindowAction.OpenReleasePage:
                UpdateWindow.OpenReleasePage(update.ReleasePageUrl);
                break;
            case UpdateWindowAction.UpdateNow:
                StartUpdater(update);
                break;
        }
    }

    private void StartUpdater(UpdateCheckResult update)
    {
        if (update.PackageAsset is not UpdateAsset packageAsset)
        {
            System.Windows.MessageBox.Show(
                "当前 Release 没有找到可自动更新的 win-x64 便携包，将打开发布页供你手动下载。",
                "立即更新",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            UpdateWindow.OpenReleasePage(update.ReleasePageUrl);
            return;
        }

        string rootDirectory = GetPackageRootDirectory();
        string updaterPath = Path.Combine(rootDirectory, "OWTranslatorLiteUpdater.exe");
        if (!File.Exists(updaterPath))
        {
            System.Windows.MessageBox.Show(
                "当前目录没有找到 OWTranslatorLiteUpdater.exe。\n\n请打开发布页手动下载最新版本，或确认发布包完整解压。",
                "立即更新",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            UpdateWindow.OpenReleasePage(update.ReleasePageUrl);
            return;
        }

        string launcherPath = Path.Combine(rootDirectory, "OWTranslatorLite.exe");
        ProcessStartInfo startInfo = new()
        {
            FileName = updaterPath,
            WorkingDirectory = rootDirectory,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(rootDirectory);
        startInfo.ArgumentList.Add("--download-url");
        startInfo.ArgumentList.Add(packageAsset.DownloadUrl);
        if (update.Sha256Asset is UpdateAsset sha256Asset)
        {
            startInfo.ArgumentList.Add("--sha256-url");
            startInfo.ArgumentList.Add(sha256Asset.DownloadUrl);
        }

        startInfo.ArgumentList.Add("--release-page");
        startInfo.ArgumentList.Add(update.ReleasePageUrl);
        startInfo.ArgumentList.Add("--launcher");
        startInfo.ArgumentList.Add(launcherPath);
        startInfo.ArgumentList.Add("--pid");
        startInfo.ArgumentList.Add(Process.GetCurrentProcess().Id.ToString());

        Process.Start(startInfo);
        AddLog($"已启动更新器：{update.LatestVersion}");
        System.Windows.Application.Current.Shutdown();
    }

    private void ClearUserData_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult result = System.Windows.MessageBox.Show(
            "这会暂停识别，清空本机设置、API Key、日志、诊断文件和 overlay 历史，并恢复默认配置。\n\n继续清除？",
            "清除本机数据",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        StopLoop(hideOverlay: true, clearOverlay: true);
        _overlayController.Hide();
        LogList.Items.Clear();
        _config.ResetUserData();
        _coordinator = CreateCoordinator();
        _frameDiffGate.Reset();
        _burstOcrFramesRemaining = 0;
        InvalidateOcrEngine();
        _activeRunSettingsKey = null;
        _pausedAt = null;
        _lastTranslationCompletedAt = null;
        _overlayHiddenByIdle = false;
        _consecutiveNoChatFrames = 0;
        _isAdjustingTranslationFrame = false;
        LoadSettingsToUi();
        ApplyFrameAdjustmentState();
        ApplyRunningState();
        AddLog("本机数据已清除，已恢复默认配置。");
    }

    private void SaveOverlayBounds(AppSettings settings)
    {
        _overlayController.SaveBoundsTo(settings);
    }

    private async Task RunLoopAsync(int generation, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && IsActiveGeneration(generation))
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool ranOcr = false;
            try
            {
                if (_config.Settings.CaptureRegion is null)
                {
                    break;
                }

                System.Windows.Rect captureRegion = _config.Settings.CaptureRegion.ToRect();
                using System.Drawing.Bitmap bitmap = ScreenCaptureService.Capture(captureRegion);
                FrameDiffResult diff = _frameDiffGate.Observe(bitmap);
                if (_burstOcrFramesRemaining <= 0 && diff.HasChanged)
                {
                    TextPresenceResult textPresence = _textPresenceGate.Observe(bitmap);
                    bool heartbeat = DateTime.Now - _lastTextGateOcrAt >= TextGateHeartbeatInterval;
                    if (textPresence.HasLikelyText || heartbeat)
                    {
                        _burstOcrFramesRemaining = BurstOcrFrameCount;
                        _lastTextGateOcrAt = DateTime.Now;
                        _consecutiveTextGateRejects = 0;
                        AppendDedupeLog(textPresence.HasLikelyText
                            ? $"text-gate accepted score={textPresence.Score:0.##} components={textPresence.CandidateComponentCount} lines={textPresence.CandidateLineCount} reason={textPresence.Reason}"
                            : $"text-gate heartbeat score={textPresence.Score:0.##} components={textPresence.CandidateComponentCount} lines={textPresence.CandidateLineCount} reason={textPresence.Reason}");
                    }
                    else
                    {
                        _consecutiveTextGateRejects++;
                        if (_consecutiveTextGateRejects % 10 == 1)
                        {
                            AppendDedupeLog($"text-gate rejected score={textPresence.Score:0.##} components={textPresence.CandidateComponentCount} lines={textPresence.CandidateLineCount} rejects={_consecutiveTextGateRejects} reason={textPresence.Reason}");
                        }
                    }
                }

                if (_burstOcrFramesRemaining > 0)
                {
                    ranOcr = true;
                    _burstOcrFramesRemaining--;
                    IReadOnlyList<ParsedChatLine> newLines = await _ocrEngineManager.UseAsync(
                        _config.Settings.OcrEngine,
                        _config.Settings.OcrLanguage,
                        (engine, token) => _coordinator.DetectNewLinesFromBitmapAsync(engine, bitmap, captureRegion, token),
                        cancellationToken);

                    if (!IsActiveGeneration(generation))
                    {
                        break;
                    }

                    _recentChatLanguages.Record(_coordinator.LastVisibleChatLines);
                    if (newLines.Count > 0)
                    {
                        EnqueueTranslationLines(newLines, generation, cancellationToken);
                    }

                    Dispatcher.Invoke(() =>
                    {
                        if (IsActiveGeneration(generation))
                        {
                            ApplyChatVisibilityLevel(_coordinator.HasVisibleChat);
                        }
                    });
                }

                stopwatch.Stop();
                Dispatcher.Invoke(() =>
                {
                    if (!IsActiveGeneration(generation))
                    {
                        return;
                    }

                    MaybeHideOverlayAfterIdle();
                    RefreshReplyTargetDisplay();
                    LatencyText.Text = ranOcr
                        ? $"{stopwatch.ElapsedMilliseconds} ms OCR"
                        : $"{stopwatch.ElapsedMilliseconds} ms patrol";
                    RefreshRuntimeMetrics();
                });
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (InvalidCaptureRegionException ex)
            {
                Dispatcher.Invoke(() =>
                {
                    if (IsActiveGeneration(generation))
                    {
                        AddLog($"错误：{ex.Message}");
                        StopLoop(hideOverlay: true, clearOverlay: false);
                        StatusText.Text = "已暂停";
                    }
                });
                break;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    if (IsActiveGeneration(generation))
                    {
                        AddLog($"错误：{ex.Message}");
                    }
                });
            }

            int delay = GetSamplingDelayMs();
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void EnqueueTranslationLines(IReadOnlyList<ParsedChatLine> lines, int generation, CancellationToken cancellationToken)
    {
        List<ParsedChatLine> skipped = [];
        int queuedCount;
        lock (_translationQueueLock)
        {
            foreach (ParsedChatLine line in lines)
            {
                _translationQueue.Enqueue(line);
            }

            while (_translationQueue.Count > TranslationQueueHardLimit)
            {
                skipped.Add(_translationQueue.Dequeue());
            }

            queuedCount = _translationQueue.Count;
        }

        _translationQueueStatus.SetQueuedCount(queuedCount);
        Dispatcher.Invoke(() =>
        {
            if (IsActiveGeneration(generation))
            {
                RefreshRuntimeMetrics();
            }
        });

        if (skipped.Count > 0)
        {
            _coordinator.ReleasePendingTranslations(skipped);
            Dispatcher.Invoke(() =>
            {
                if (IsActiveGeneration(generation))
                {
                    AddLog($"翻译队列超过安全上限，已跳过 {skipped.Count} 条最旧消息。");
                }
            });
        }

        EnsureTranslationWorker(generation, cancellationToken);
    }

    private void EnsureTranslationWorker(int generation, CancellationToken cancellationToken)
    {
        lock (_translationQueueLock)
        {
            if (_translationWorkerTask is not null && !_translationWorkerTask.IsCompleted)
            {
                return;
            }

            _translationWorkerTask = Task.Run(() => RunTranslationWorkerAsync(generation, cancellationToken), CancellationToken.None);
        }
    }

    private async Task RunTranslationWorkerAsync(int generation, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && IsActiveGeneration(generation))
            {
                List<ParsedChatLine> batch = await DequeueTranslationBatchAsync(cancellationToken);
                if (batch.Count == 0)
                {
                    break;
                }

                try
                {
                    _translationQueueStatus.BeginBatch(batch.Count);
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    IReadOnlyList<TranslationRecord> records = await _coordinator.TranslateAsync(batch, cancellationToken);
                    stopwatch.Stop();
                    _translationQueueStatus.CompleteBatch(batch.Count, stopwatch.Elapsed);
                    if (records.Count > 0 && IsActiveGeneration(generation))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (!IsActiveGeneration(generation))
                            {
                                return;
                            }

                            AddTranslationRecords(records);
                            LatencyText.Text = $"{stopwatch.ElapsedMilliseconds} ms API";
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    _translationQueueStatus.CancelBatch(batch.Count);
                    _coordinator.ReleasePendingTranslations(batch);
                    break;
                }
                catch (Exception ex)
                {
                    _translationQueueStatus.FailBatch(batch.Count, ex.Message);
                    IReadOnlyList<ParsedChatLine> retryLines = _coordinator.MarkTranslationFailedForRetry(batch, MaxTranslationRetries);
                    if (retryLines.Count > 0 && IsActiveGeneration(generation))
                    {
                        EnqueueTranslationLines(retryLines, generation, cancellationToken);
                    }

                    Dispatcher.Invoke(() =>
                    {
                        if (IsActiveGeneration(generation))
                        {
                            string retrySuffix = retryLines.Count > 0
                                ? $"，将重试 {retryLines.Count} 条。"
                                : "，已达到重试上限。";
                            AddLog($"翻译请求失败：{ex.Message}{retrySuffix}");
                        }
                    });
                }
            }
        }
        finally
        {
            bool shouldRestart;
            lock (_translationQueueLock)
            {
                _translationWorkerTask = null;
                shouldRestart = _isRunning && IsActiveGeneration(generation) && _translationQueue.Count > 0;
            }

            if (shouldRestart && _loopCts is CancellationTokenSource cts)
            {
                EnsureTranslationWorker(generation, cts.Token);
            }
        }
    }

    private Task<List<ParsedChatLine>> DequeueTranslationBatchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<ParsedChatLine> batch = [];
        int batchLimit;
        lock (_translationQueueLock)
        {
            if (_translationQueue.Count == 0)
            {
                _translationQueueStatus.SetQueuedCount(0);
                return Task.FromResult(batch);
            }

            batchLimit = _translationQueue.Count >= TranslationQueueSoftBatchThreshold
                ? MaxOverflowTranslationBatchSize
                : MaxTranslationBatchSize;
            batch.Add(_translationQueue.Dequeue());
            _translationQueueStatus.SetQueuedCount(_translationQueue.Count);
        }

        lock (_translationQueueLock)
        {
            while (batch.Count < batchLimit && _translationQueue.Count > 0)
            {
                batch.Add(_translationQueue.Dequeue());
            }

            _translationQueueStatus.SetQueuedCount(_translationQueue.Count);
            Dispatcher.Invoke(() => RefreshRuntimeMetrics());
        }

        return Task.FromResult(batch);
    }

    private void AddTranslationRecords(IReadOnlyList<TranslationRecord> records)
    {
        int addedCount = 0;
        foreach (TranslationRecord record in records)
        {
            _records.Add(record);
            addedCount++;
            AddLog($"{record.Speaker}: {record.SourceText}  =>  {record.TranslatedText}");
        }

        if (addedCount == 0)
        {
            return;
        }

        _records.Sort(static (left, right) =>
        {
            int seqCompare = left.Seq.CompareTo(right.Seq);
            return seqCompare != 0
                ? seqCompare
                : left.Timestamp.CompareTo(right.Timestamp);
        });
        TrimOverlayRecords();
        _lastTranslationCompletedAt = DateTime.Now;
        _overlayHiddenByIdle = false;
        EnsureOverlay();
        _overlayController.Show();
        _overlayController.UpdateRecords(_records);
        UpdateRecentRecords();
        RefreshRuntimeMetrics();
    }

    private void RestartLoop(bool resetChatCycle, bool resetOcrEngine, string message)
    {
        StopLoop(hideOverlay: false, clearOverlay: false);

        if (_config.Settings.CaptureRegion is null)
        {
            StatusText.Text = "未选择区域";
            AddLog("请先选择聊天区域。");
            return;
        }

        if (resetChatCycle)
        {
            _coordinator.ResetChatCycle();
            _frameDiffGate.Reset();
            _burstOcrFramesRemaining = 0;
            _consecutiveNoChatFrames = 0;
            _consecutiveTextGateRejects = 0;
            _lastTextGateOcrAt = DateTime.Now;
        }

        if (resetOcrEngine)
        {
            InvalidateOcrEngine();
        }

        EnsureOverlay();
        int generation = Interlocked.Increment(ref _runGeneration);
        _loopCts = new CancellationTokenSource();
        _isRunning = true;
        _pausedAt = null;
        _overlayHiddenByIdle = false;
        _consecutiveNoChatFrames = 0;
        _activeRunSettingsKey = CreateRunSettingsKey();
        StatusText.Text = "运行中";
        ApplyRunningState();
        RefreshRuntimeMetrics();
        AddLog(message);
        _ = RunLoopAsync(generation, _loopCts.Token);
    }

    private void StopLoop(bool hideOverlay, bool clearOverlay)
    {
        if (hideOverlay)
        {
            ExitReplyMode();
        }

        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = null;
        Interlocked.Increment(ref _runGeneration);
        _isRunning = false;
        _burstOcrFramesRemaining = 0;
        _consecutiveNoChatFrames = 0;
        _consecutiveTextGateRejects = 0;
        _lastTextGateOcrAt = DateTime.Now;
        ClearTranslationQueue();
        _coordinator?.ClearPendingTranslations();
        ApplyRunningState();
        RefreshRuntimeMetrics();

        if (clearOverlay)
        {
            ClearOverlayRecords();
        }

        if (hideOverlay)
        {
            _pausedAt = DateTime.Now;
            _overlayHiddenByIdle = false;
            _consecutiveNoChatFrames = 0;
            if (_config.Settings.KeepOverlayVisible)
            {
                ApplyOverlayVisibilityPreference(activate: false);
            }
            else
            {
                _overlayController.Hide();
            }
        }
    }

    private void EnsureOverlay()
    {
        bool wasCreated = _overlayController.IsCreated;
        _overlayController.Ensure(_config.Settings);
        if (_config.Settings.CaptureRegion is CaptureRegion region)
        {
            if (!wasCreated)
            {
                _overlayController.MoveNear(region.ToRect());
            }
        }
    }

    private void ApplyOverlaySettings()
    {
        _overlayController.ApplySettings(_config.Settings);
    }

    private void ApplyOverlayVisibilityPreference(bool activate)
    {
        if (_config.Settings.KeepOverlayVisible)
        {
            EnsureOverlay();
            ApplyOverlaySettings();
            _overlayController.UpdateRecords(_records);
            if (activate)
            {
                _overlayController.ShowAndActivate();
            }
            else
            {
                _overlayController.Show();
            }

            _overlayHiddenByIdle = false;
            return;
        }

        if (!_isAdjustingTranslationFrame && !_isReplyModeActive)
        {
            _overlayController.Hide();
            _overlayHiddenByIdle = true;
        }
    }

    private void Overlay_BoundsChanged(object? sender, EventArgs e)
    {
        SaveOverlayBounds(_config.Settings);
        _config.Save();
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
        _diagnostics.AppendRuntimeLog(line);
        LogList.Items.Add(line);
        while (LogList.Items.Count > MaxLogRecords)
        {
            LogList.Items.RemoveAt(0);
        }

        LogList.ScrollIntoView(line);
    }

    private void AppendDedupeLog(string message)
    {
        if (!_config.Settings.EnableDebugDiagnostics)
        {
            return;
        }

        _diagnostics.AppendDebugLog(message);
    }

    private RuntimeDiagnosticsSnapshot CreateRuntimeDiagnosticsSnapshot()
    {
        return new RuntimeDiagnosticsSnapshot(
            _isRunning,
            Volatile.Read(ref _runGeneration),
            _records.Count,
            _translationQueueStatus.Snapshot());
    }

    private async void Overlay_ReplySubmitted(object? sender, ReplySubmittedEventArgs e)
    {
        if (_replyTranslationCts is not null)
        {
            _overlayController.SetReplyStatus("上一句还在翻译");
            return;
        }

        string targetLanguage = e.SelectedLanguage == "auto" ? ResolveReplyTargetLanguage() : e.SelectedLanguage;
        _overlayController.SetReplyTargetLanguage(_config.Settings.ReplyTargetLanguage, targetLanguage);
        _overlayController.SetReplyStatus("翻译中...");
        AddLog($"回话翻译：{GetLanguageLabel(targetLanguage)} <= {e.SourceText}");

        _replyTranslationCts = new CancellationTokenSource();
        try
        {
            OpenAICompatibleTranslationProvider provider = new(_config.Settings, _glossary);
            string translated = await provider.TranslateOutgoingReplyAsync(
                e.SourceText,
                targetLanguage,
                _replyTranslationCts.Token);

            if (string.IsNullOrWhiteSpace(translated))
            {
                _overlayController.SetReplyStatus("翻译为空");
                return;
            }

            if (!_config.Settings.AutoCopyReplyTranslation)
            {
                _overlayController.SetReplyTranslation(translated);
                _overlayController.SetReplyStatus($"已翻译：{LimitReplyStatus(translated)}");
                _isReplyModeActive = true;
                AddLog($"回话已翻译：{translated}");
                return;
            }

            ClipboardSetResult copyResult = await _clipboardService.SetTextWithRetryAsync(
                translated,
                _replyTranslationCts.Token);
            if (copyResult.Succeeded)
            {
                _overlayController.ClearReplyInput();
                _overlayController.SetReplyStatus($"已复制：{LimitReplyStatus(translated)}");
                _isReplyModeActive = false;
                AddLog($"回话已复制：{translated}");
                return;
            }

            _overlayController.SetReplyTranslation(translated);
            _overlayController.SetReplyStatus("译文已生成，剪贴板被占用，请手动复制");
            _isReplyModeActive = true;
            AddLog($"回话已翻译但复制失败：{FormatClipboardFailure(copyResult)}");
        }
        catch (OperationCanceledException)
        {
            _overlayController.SetReplyStatus("已取消");
        }
        catch (Exception ex)
        {
            _overlayController.SetReplyStatus($"失败：{LimitReplyStatus(ex.Message)}");
            AddLog($"回话翻译失败：{ex.Message}");
        }
        finally
        {
            _replyTranslationCts?.Dispose();
            _replyTranslationCts = null;
            _overlayController.SetReplyTargetLanguage(_config.Settings.ReplyTargetLanguage, ResolveReplyTargetLanguage());
        }
    }

    private async void Overlay_CopyReplyRequested(object? sender, string translated)
    {
        if (string.IsNullOrWhiteSpace(translated))
        {
            _overlayController.SetReplyStatus("没有可复制的译文");
            return;
        }

        try
        {
            ClipboardSetResult result = await _clipboardService.SetTextWithRetryAsync(translated, CancellationToken.None);
            if (result.Succeeded)
            {
                _overlayController.ClearReplyInput();
                _overlayController.SetReplyStatus($"已复制：{LimitReplyStatus(translated)}");
                _isReplyModeActive = false;
                AddLog($"回话已复制：{translated}");
                return;
            }

            _overlayController.SetReplyStatus("剪贴板被占用，请稍后再试");
            AddLog($"回话手动复制失败：{FormatClipboardFailure(result)}");
        }
        catch (Exception ex)
        {
            _overlayController.SetReplyStatus($"复制失败：{LimitReplyStatus(ex.Message)}");
            AddLog($"回话手动复制失败：{ex.Message}");
        }
    }

    private static string FormatClipboardFailure(ClipboardSetResult result)
    {
        string owner = string.IsNullOrWhiteSpace(result.OwnerDescription)
            ? "未知占用方"
            : result.OwnerDescription;
        return $"{result.ErrorMessage}；尝试 {result.Attempts} 次；占用：{owner}";
    }

    private void Overlay_ReplyTargetLanguageChanged(object? sender, string language)
    {
        _config.Settings.ReplyTargetLanguage = NormalizeReplyLanguage(language);
        _config.Save();
        RefreshReplyTargetDisplay();
    }

    private void Overlay_ReplyEditingStarted(object? sender, EventArgs e)
    {
        _isReplyModeActive = true;
        _overlayHiddenByIdle = false;
        RefreshReplyTargetDisplay();
    }

    private void Overlay_ReplyModeExited(object? sender, EventArgs e)
    {
        ExitReplyMode();
    }

    private void EnterReplyMode()
    {
        EnsureOverlay();
        ApplyOverlaySettings();
        _isReplyModeActive = true;
        _overlayHiddenByIdle = false;
        _overlayController.EnterReplyMode(_config.Settings.ReplyTargetLanguage, ResolveReplyTargetLanguage());
        AddLog("已进入回话模式。");
    }

    private void ExitReplyMode()
    {
        if (!_isReplyModeActive)
        {
            return;
        }

        _isReplyModeActive = false;
        _replyTranslationCts?.Cancel();
        _overlayController.ExitReplyMode();
        AddLog("已退出回话模式。");
    }

    private void ToggleReplyMode()
    {
        if (_isReplyModeActive)
        {
            ExitReplyMode();
            return;
        }

        EnterReplyMode();
    }

    private void RefreshReplyTargetDisplay()
    {
        if (!_isReplyModeActive || !_overlayController.IsCreated)
        {
            return;
        }

        _overlayController.SetReplyTargetLanguage(_config.Settings.ReplyTargetLanguage, ResolveReplyTargetLanguage());
    }

    private string ResolveReplyTargetLanguage()
    {
        string selected = NormalizeReplyLanguage(_config.Settings.ReplyTargetLanguage);
        return selected == "auto"
            ? _recentChatLanguages.DetectOrDefault("en")
            : selected;
    }

    private static string NormalizeReplyLanguage(string language)
    {
        return language is "en" or "ja" or "ko" ? language : "auto";
    }

    private int GetSamplingDelayMs() =>
        Math.Clamp(_config.Settings.CaptureIntervalMs, MinSamplingIntervalMs, MaxSamplingIntervalMs);

    private static void NormalizeReplySettings(AppSettings settings)
    {
        settings.ReplyTargetLanguage = NormalizeReplyLanguage(settings.ReplyTargetLanguage);
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

    private static string LimitReplyStatus(string value)
    {
        string trimmed = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return trimmed.Length <= 36 ? trimmed : trimmed[..36] + "...";
    }

    private void ApplyReplyHotkeyRegistration()
    {
        HotKeyRegistrationResult result = _replyHotKey.Apply(
            new WindowInteropHelper(this).Handle,
            _config.Settings.EnableReplyHotkey,
            _config.Settings.ReplyHotkey);
        switch (result.Status)
        {
            case HotKeyRegistrationStatus.Registered:
                AddLog($"回话热键已启用：{result.Gesture}。");
                break;
            case HotKeyRegistrationStatus.InvalidGesture:
                AddLog($"回话热键配置无效：{result.Gesture}");
                break;
            case HotKeyRegistrationStatus.WindowHandleUnavailable:
                AddLog("回话热键注册失败：窗口句柄不可用。");
                break;
            case HotKeyRegistrationStatus.RegistrationFailed:
                AddLog("回话热键注册失败，可能已被其他程序占用。");
                break;
        }
    }

    private void EnsureDefaultModelOptions()
    {
        AddModelOption("deepseek-v4-flash");
        AddModelOption("deepseek-v4-pro");
    }

    private void AddModelOption(string model)
    {
        foreach (object? item in ModelCombo.Items)
        {
            if (string.Equals(item?.ToString(), model, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        ModelCombo.Items.Add(model);
    }

    private static void NormalizeOcrSettings(AppSettings settings)
    {
        settings.OcrEngine = "OneOCR";
        settings.OcrLanguage = "auto";
        if (settings.TranslationProvider is "Local" or "Local Rules")
        {
            settings.TranslationProvider = "DeepSeek";
        }
    }

    private void ClearOverlayRecords()
    {
        _records.Clear();
        _lastTranslationCompletedAt = null;
        _overlayHiddenByIdle = false;
        _consecutiveNoChatFrames = 0;
        _overlayController.UpdateRecords(_records);
        UpdateRecentRecords();
        RefreshRuntimeMetrics();
    }

    private void ClearTranslationQueue()
    {
        lock (_translationQueueLock)
        {
            _translationQueue.Clear();
        }

        _translationQueueStatus.Reset();
    }

    private void TrimOverlayRecords()
    {
        while (_records.Count > MaxOverlayRecords)
        {
            _records.RemoveAt(0);
        }
    }

    private void MaybeHideOverlayAfterIdle()
    {
        if (!_isRunning || !_overlayController.IsCreated)
        {
            return;
        }

        if (_config.Settings.KeepOverlayVisible)
        {
            return;
        }

        if (_overlayHiddenByIdle || _consecutiveNoChatFrames < 2)
        {
            return;
        }

        if (_lastTranslationCompletedAt is not DateTime completedAt ||
            DateTime.Now - completedAt < OverlayIdleHideDelay)
        {
            return;
        }

        _overlayController.Hide();
        _overlayHiddenByIdle = true;
    }

    private void ApplyChatVisibilityLevel(bool hasVisibleChat)
    {
        if (!_isRunning)
        {
            return;
        }

        if (!hasVisibleChat)
        {
            _consecutiveNoChatFrames++;
            MaybeHideOverlayAfterIdle();
            return;
        }

        _consecutiveNoChatFrames = 0;
        _overlayHiddenByIdle = false;
        EnsureOverlay();
        _overlayController.UpdateRecords(_records);
        _overlayController.Show();
    }

    private void InvalidateOcrEngine()
    {
        _ocrEngineManager.Invalidate();
    }

    private bool IsActiveGeneration(int generation) =>
        _isRunning && Volatile.Read(ref _runGeneration) == generation;

    private void ApplyRunningState()
    {
        if (StartButton is null || StopButton is null || AdjustFrameButton is null)
        {
            return;
        }

        StartButton.IsEnabled = !_isRunning;
        StopButton.IsEnabled = _isRunning;
        AdjustFrameButton.IsEnabled = true;
        RefreshRuntimeMetrics();
    }

    private void ApplyFrameAdjustmentState()
    {
        if (AdjustFrameButton is null || FrameAdjustHint is null)
        {
            return;
        }

        AdjustFrameButton.Content = _isAdjustingTranslationFrame
            ? "完成调整"
            : "调整翻译框";
        if (_isAdjustingTranslationFrame)
        {
            AdjustFrameButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "PrimaryBrush");
            AdjustFrameButton.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "PrimaryBrush");
            AdjustFrameButton.Foreground = System.Windows.Media.Brushes.White;
        }
        else
        {
            AdjustFrameButton.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            AdjustFrameButton.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
            AdjustFrameButton.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
        }

        FrameAdjustHint.Visibility = _isAdjustingTranslationFrame
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private string CreateRunSettingsKey()
    {
        CaptureRegion? region = _config.Settings.CaptureRegion;
        string regionKey = region is null
            ? "none"
            : $"{region.Left:0.##},{region.Top:0.##},{region.Width:0.##},{region.Height:0.##}";
        return string.Join("|",
            _config.Settings.OcrEngine,
            _config.Settings.OcrLanguage,
            _config.Settings.TranslationProvider,
            _config.Settings.ApiUrl,
            _config.Settings.Model,
            regionKey);
    }

    private void RefreshRuntimeMetrics()
    {
        if (QueueMetricText is null)
        {
            return;
        }

        TranslationQueueStatus queue = _translationQueueStatus.Snapshot();
        QueueMetricText.Text = queue.QueuedCount.ToString();
    }

    private void UpdateRecentRecords()
    {
        if (RecentRecordList is null)
        {
            return;
        }

        RecentRecordList.ItemsSource = _records
            .TakeLast(12)
            .Reverse<TranslationRecord>()
            .ToList();
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private static bool ContainsCjk(string value)
    {
        foreach (char ch in value)
        {
            if ((ch >= '\u3400' && ch <= '\u9fff') ||
                (ch >= '\uf900' && ch <= '\ufaff'))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetPackageRootDirectory()
    {
        string baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        DirectoryInfo? directory = new(baseDirectory);
        if (directory.Name.Equals("app", StringComparison.OrdinalIgnoreCase) &&
            directory.Parent is not null)
        {
            return directory.Parent.FullName;
        }

        return baseDirectory;
    }

    private static string CreateIssueTemplate()
    {
        string version = UpdateService.GetCurrentVersion();
        string installPath = GetPackageRootDirectory();
        string hasCjkPath = ContainsCjk(installPath) ? "是" : "否";
        return $"""
               ## 问题描述
               请描述你遇到的问题、发生场景和复现步骤。

               ## 环境
               - OW Translator Lite: {version}
               - Windows: {Environment.OSVersion}
               - 安装路径包含中文: {hasCjkPath}

               ## 诊断文件
               如需排查，请在主窗口左侧诊断工具中点击“导出反馈包”，并把生成的 zip 拖到这个 Issue 中。
               """;
    }

    private static void OpenShellPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
            // Opening the folder is convenience-only.
        }
    }

}
