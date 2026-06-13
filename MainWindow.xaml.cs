using System.Diagnostics;
using System.IO;
using System.Reflection;
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

    private readonly ConfigStore _config = new();
    private readonly RecentChatLanguageTracker _recentChatLanguages = new();
    private readonly HotKeyService _replyHotKey = new(ReplyHotkeyId);
    private readonly DiagnosticsService _diagnostics = new();
    private readonly FrameSequenceRecorder _frameSequenceRecorder = new();
    private readonly OverlayController _overlayController = new();
    private OwGlossaryService _glossary = null!;
    private TranslationCoordinator _coordinator = null!;
    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource? _replyTranslationCts;
    private CancellationTokenSource? _fetchModelsCts;
    private readonly OcrEngineManager _ocrEngineManager = new();
    private readonly FrameDiffGate _frameDiffGate = new();
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
    private bool _isAdjustingTranslationFrame;
    private int _burstOcrFramesRemaining;
    private int _consecutiveNoChatFrames;
    private int _runGeneration;
    private readonly List<TranslationRecord> _records = [];

    public MainWindow()
    {
        InitializeComponent();
        ModelCombo.AddHandler(WpfTextBoxBase.TextChangedEvent, new TextChangedEventHandler(TranslationSettings_Changed));
        _replyHotKey.Pressed += (_, _) => ToggleReplyMode();
        _overlayController.BoundsChangedByUser += Overlay_BoundsChanged;
        _overlayController.ReplySubmitted += Overlay_ReplySubmitted;
        _overlayController.ReplyEditingStarted += Overlay_ReplyEditingStarted;
        _overlayController.ReplyTargetLanguageChanged += Overlay_ReplyTargetLanguageChanged;
        _overlayController.ReplyModeExited += Overlay_ReplyModeExited;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _config.Load();
        _glossary = OwGlossaryService.LoadDefault();
        _coordinator = CreateCoordinator();
        LoadSettingsToUi();
        EnsureOverlay();
        ApplyRunningState();
        ApplyFrameAdjustmentState();
        AddLog("就绪。正式测试建议使用 DeepSeek API。");
        ApplyReplyHotkeyRegistration();
        ShowQuickStartIfNeeded();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        EndFrameAdjustment(log: false);
        ExitReplyMode();
        _replyHotKey.Dispose();
        _replyTranslationCts?.Cancel();
        _fetchModelsCts?.Cancel();
        _frameSequenceRecorder.Stop();
        StopLoop(hideOverlay: false, clearOverlay: false);
        _ocrEngineManager.Dispose();
        SaveSettingsFromUi();
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
            SelectCombo(OcrEngineCombo, settings.OcrEngine);
            SelectCombo(OcrLanguageCombo, settings.OcrLanguage);
            SelectCombo(ProviderCombo, settings.TranslationProvider);
            EnsureDefaultModelOptions();
            ApiUrlBox.Text = settings.ApiUrl;
            ApiKeyBox.Password = settings.ApiKey;
            ModelCombo.Text = settings.Model;
            FontSizeSlider.Value = settings.OverlayFontSize;
            OpacitySlider.Value = settings.OverlayOpacity;
            ClickThroughCheck.IsChecked = settings.OverlayClickThrough;
            ReplyInputBarCheck.IsChecked = settings.ShowReplyInputBar;
            ReplyHotkeyCheck.IsChecked = settings.EnableReplyHotkey;
            SelectCombo(ReplyHotkeyCombo, settings.ReplyHotkey);
            DedupeDebugCheck.IsChecked = settings.EnableDedupeDebugLog;
            SaveScreenshotsCheck.IsChecked = settings.SaveScreenshotsOnTranslation;
            FirstRunPanel.Visibility = settings.FirstRun ? Visibility.Visible : Visibility.Collapsed;
            UpdateProviderPreset();
            UpdateRegionText();
            RefreshStatusChips();
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void SaveSettingsFromUi()
    {
        AppSettings settings = _config.Settings;
        settings.OcrEngine = GetComboText(OcrEngineCombo);
        settings.OcrLanguage = GetComboText(OcrLanguageCombo);
        settings.TranslationProvider = GetComboText(ProviderCombo);
        settings.ApiUrl = ApiUrlBox.Text.Trim();
        settings.ApiKey = ApiKeyBox.Password.Trim();
        settings.Model = ModelCombo.Text.Trim();
        settings.OverlayFontSize = FontSizeSlider.Value;
        settings.OverlayOpacity = OpacitySlider.Value;
        settings.OverlayClickThrough = ClickThroughCheck.IsChecked == true;
        settings.ShowReplyInputBar = ReplyInputBarCheck.IsChecked == true;
        settings.EnableReplyHotkey = ReplyHotkeyCheck.IsChecked == true;
        settings.ReplyHotkey = GetComboText(ReplyHotkeyCombo);
        settings.EnableDedupeDebugLog = DedupeDebugCheck.IsChecked == true;
        settings.SaveScreenshotsOnTranslation = SaveScreenshotsCheck.IsChecked == true;
        SaveOverlayBounds(settings);
        _config.Save();
        ApplyOverlaySettings();
        RefreshStatusChips();
    }

    private void ApplyScreenshotSaveDirectory()
    {
        if (_config.Settings.SaveScreenshotsOnTranslation)
        {
            string repoRoot = AppContext.BaseDirectory;
            while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot, "OwTranslateLite.csproj")))
            {
                repoRoot = Path.GetDirectoryName(repoRoot)!;
            }

            _coordinator.ScreenshotSaveDirectory = repoRoot is not null
                ? Path.Combine(repoRoot, "captured-screenshots")
                : null;
        }
        else
        {
            _coordinator.ScreenshotSaveDirectory = null;
        }
    }

    private TranslationCoordinator CreateCoordinator() =>
        new(_config.Settings, _glossary, AppendDedupeLog)
        {
            FrameSequenceRecorder = _frameSequenceRecorder
        };

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

    private void BetaDebugSettings_Changed(object sender, RoutedEventArgs e)
    {
        AutoSaveSettings();
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
        ApplyScreenshotSaveDirectory();
    }

    private void OcrSettings_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isLoadingSettings)
        {
            return;
        }

        SaveSettingsFromUi();
        _coordinator.ResetChatCycle();
        InvalidateOcrEngine();

        if (_isRunning)
        {
            RestartLoop(resetChatCycle: true, resetOcrEngine: true, "OCR 设置已更新，已重启识别。");
        }
        else
        {
            _activeRunSettingsKey = null;
            AddLog("OCR 设置已更新，下次开始时生效。");
        }
    }

    private void FinishFirstRun_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        _config.Settings.FirstRun = false;
        _config.Save();
        FirstRunPanel.Visibility = Visibility.Collapsed;
        AddLog("首次配置完成。");
    }

    private void ShowQuickStartIfNeeded()
    {
        if (!_config.Settings.ShowQuickStart)
        {
            return;
        }

        QuickStartWindow quickStart = new()
        {
            Owner = this
        };
        quickStart.ShowDialog();
        if (quickStart.DoNotShowAgain)
        {
            _config.Settings.ShowQuickStart = false;
            _config.Save();
        }
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

    private void ShowOverlay_Click(object sender, RoutedEventArgs e)
    {
        EnsureOverlay();
        ApplyOverlaySettings();
        _overlayController.ShowAndActivate();
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
        ApplyScreenshotSaveDirectory();
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
            RefreshStatusChips();
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

    private void OpenRuntimeLog_Click(object sender, RoutedEventArgs e)
    {
        _diagnostics.OpenRuntimeLog();
        AddLog($"已打开日志：{ConfigStore.RuntimeLogPath}");
    }

    private void OpenDedupeLog_Click(object sender, RoutedEventArgs e)
    {
        _diagnostics.OpenDedupeLog();
        AddLog($"已打开去重日志：{ConfigStore.DedupeLogPath}");
    }

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        string diagnosticsPath = _diagnostics.ExportDiagnostics(
            _config.Settings,
            LogList.Items.Cast<object>().Select(static item => item.ToString() ?? ""),
            CreateRuntimeDiagnosticsSnapshot());
        AddLog($"已导出诊断：{diagnosticsPath}");
    }

    private void FrameRecording_Click(object sender, RoutedEventArgs e)
    {
        if (_frameSequenceRecorder.IsRecording)
        {
            string? sessionDirectory = _frameSequenceRecorder.Stop();
            UpdateFrameRecordingUi();
            if (!string.IsNullOrWhiteSpace(sessionDirectory))
            {
                AddLog($"已结束 Case 录制：{sessionDirectory}");
                OpenShellPath(sessionDirectory);
            }

            return;
        }

        EndFrameAdjustment(log: true);
        SaveSettingsFromUi();
        ApplyScreenshotSaveDirectory();
        if (_config.Settings.CaptureRegion is null)
        {
            AddLog("请先选择聊天区域，再开始录制 Case。");
            return;
        }

        string caseId = GetComboText(FrameCaseCombo);
        string sessionPath = _frameSequenceRecorder.Start(
            caseId,
            _config.Settings.CaptureRegion,
            _config.Settings.CaptureIntervalMs);
        _coordinator.FrameSequenceRecorder = _frameSequenceRecorder;
        UpdateFrameRecordingUi();
        AddLog($"已开始录制 {caseId}：{sessionPath}");
        AddLog("录制时请按对应 case 指南操作；完成后再次点击“停止录制”。");

        if (!_isRunning)
        {
            RestartLoop(resetChatCycle: true, resetOcrEngine: false, "已开始识别并录制 Case。");
        }
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
        _frameSequenceRecorder.Stop();
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
                if (diff.HasChanged)
                {
                    _burstOcrFramesRemaining = BurstOcrFrameCount;
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
                    RefreshStatusChips(ranOcr ? "OCR burst" : null);
                });
            }
            catch (OperationCanceledException)
            {
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
                RefreshStatusChips();
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
            Dispatcher.Invoke(() => RefreshStatusChips());
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
        RefreshStatusChips();
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
        RefreshStatusChips();
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
        ClearTranslationQueue();
        _coordinator.ClearPendingTranslations();
        ApplyRunningState();
        RefreshStatusChips();

        if (clearOverlay)
        {
            ClearOverlayRecords();
        }

        if (hideOverlay)
        {
            _pausedAt = DateTime.Now;
            _overlayHiddenByIdle = false;
            _consecutiveNoChatFrames = 0;
            _overlayController.Hide();
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
        if (!_config.Settings.EnableDedupeDebugLog)
        {
            return;
        }

        _diagnostics.AppendDedupeLog(message);
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

            System.Windows.Clipboard.SetText(translated);
            _overlayController.ClearReplyInput();
            _overlayController.SetReplyStatus($"已复制：{LimitReplyStatus(translated)}");
            _isReplyModeActive = false;
            AddLog($"回话已复制：{translated}");
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
        RefreshStatusChips();
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
        UpdateFrameRecordingUi();
        RefreshStatusChips();
    }

    private void UpdateFrameRecordingUi()
    {
        if (FrameRecordingButton is null || FrameCaseCombo is null)
        {
            return;
        }

        bool isRecording = _frameSequenceRecorder.IsRecording;
        FrameRecordingButton.Content = isRecording ? "停止录制" : "录制 Case";
        FrameRecordingButton.Background = isRecording
            ? System.Windows.Media.Brushes.LightGoldenrodYellow
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(120, 217, 149));
        FrameRecordingButton.BorderBrush = FrameRecordingButton.Background;
        FrameCaseCombo.IsEnabled = !isRecording;
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
        AdjustFrameButton.Background = _isAdjustingTranslationFrame
            ? System.Windows.Media.Brushes.LightGoldenrodYellow
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(155, 183, 240));
        AdjustFrameButton.BorderBrush = AdjustFrameButton.Background;
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

    private void RefreshStatusChips(string? samplingStatus = null)
    {
        if (RunStatePillText is null)
        {
            return;
        }

        VersionText.Text = $"v{GetAppVersionLabel()}";
        RunStatePillText.Text = _isRunning ? "运行中" : "待机";
        ApiStateChipText.Text = string.IsNullOrWhiteSpace(_config.Settings.ApiKey)
            ? "API 未配置"
            : "API 已配置";
        ModelStateChipText.Text = string.IsNullOrWhiteSpace(_config.Settings.Model)
            ? "未选择模型"
            : _config.Settings.Model;
        SamplingStateChipText.Text = samplingStatus ?? $"巡逻 {GetSamplingDelayMs()}ms";
        TranslationQueueStatus queue = _translationQueueStatus.Snapshot();
        QueueStateChipText.Text = $"队列 {queue.QueuedCount}";
        QueueMetricText.Text = queue.QueuedCount.ToString();
        GlossaryStatusText.Text = $"术语 {_glossary.EntryCount} 项 · {_glossary.Version}";
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

    private static string GetAppVersionLabel()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informationalVersion)
            ? assembly.GetName().Version?.ToString() ?? "0.0.0"
            : informationalVersion;
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

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
