using System.Diagnostics;
using System.IO;
using System.Text;
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
    private static readonly TimeSpan OverlayHistoryPeekDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TranslationBatchWindow = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan DisplayDuplicateWindow = TimeSpan.FromSeconds(90);
    private const int MaxOverlayRecords = 50;
    private const int MaxLogRecords = 200;
    private const int MaxTranslationQueueItems = 30;
    private const int MaxTranslationBatchSize = 4;
    private const int ReplyHotkeyId = 0x4F57;

    private readonly ConfigStore _config = new();
    private readonly RecentChatLanguageTracker _recentChatLanguages = new();
    private readonly HotKeyService _replyHotKey = new(ReplyHotkeyId);
    private OwGlossaryService _glossary = null!;
    private TranslationCoordinator _coordinator = null!;
    private OverlayWindow? _overlay;
    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource? _replyTranslationCts;
    private IOcrEngine? _currentOcrEngine;
    private readonly SemaphoreSlim _ocrEngineGate = new(1, 1);
    private readonly Queue<ParsedChatLine> _translationQueue = [];
    private readonly object _translationQueueLock = new();
    private Task? _translationWorkerTask;
    private string? _currentOcrEngineName;
    private string? _currentOcrLanguage;
    private string? _activeRunSettingsKey;
    private DateTime? _pausedAt;
    private DateTime? _lastTranslationCompletedAt;
    private DateTime? _historyPeekOverlayUntil;
    private bool _overlayHiddenByIdle;
    private bool _overlayVisibleForHistoryPeek;
    private bool _wasChatVisibleLastTick;
    private bool _isRunning;
    private bool _isReplyModeActive;
    private bool _isLoadingSettings;
    private bool _isApplyingOverlaySettings;
    private bool _isAdjustingTranslationFrame;
    private int _runGeneration;
    private readonly List<TranslationRecord> _records = [];

    public MainWindow()
    {
        InitializeComponent();
        ModelCombo.AddHandler(WpfTextBoxBase.TextChangedEvent, new TextChangedEventHandler(TranslationSettings_Changed));
        _replyHotKey.Pressed += (_, _) => ToggleReplyMode();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _config.Load();
        _glossary = OwGlossaryService.LoadDefault();
        _coordinator = CreateCoordinator();
        GlossaryStatusText.Text = $"术语 { _glossary.EntryCount } 项 · { _glossary.Version }";
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
        StopLoop(hideOverlay: false, clearOverlay: false);
        InvalidateOcrEngine();
        SaveSettingsFromUi();
        _overlay?.Close();
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
            ReplyHotkeyCheck.IsChecked = settings.EnableReplyHotkey;
            SelectCombo(ReplyHotkeyCombo, settings.ReplyHotkey);
            DedupeDebugCheck.IsChecked = settings.EnableDedupeDebugLog;
            FirstRunPanel.Visibility = settings.FirstRun ? Visibility.Visible : Visibility.Collapsed;
            UpdateProviderPreset();
            UpdateRegionText();
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
        settings.EnableReplyHotkey = ReplyHotkeyCheck.IsChecked == true;
        settings.ReplyHotkey = GetComboText(ReplyHotkeyCombo);
        settings.EnableDedupeDebugLog = DedupeDebugCheck.IsChecked == true;
        SaveOverlayBounds(settings);
        _config.Save();
        ApplyOverlaySettings();
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
            _overlay?.MoveNear(rect);
            AddLog($"已选择区域 {rect.Left:0},{rect.Top:0} {rect.Width:0}x{rect.Height:0}");
        };
        selector.ShowDialog();
    }

    private void ShowOverlay_Click(object sender, RoutedEventArgs e)
    {
        EnsureOverlay();
        ApplyOverlaySettings();
        _overlay?.Show();
        _overlay?.Activate();
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
        _overlay?.Show();
        _overlay?.Activate();
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
        try
        {
            AddLog("正在获取模型列表...");
            IReadOnlyList<string> models = await OpenAICompatibleTranslationProvider.FetchModelIdsAsync(
                _config.Settings,
                CancellationToken.None);

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
        }
        catch (Exception ex)
        {
            AddLog($"获取模型失败：{ex.Message}");
        }
        finally
        {
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
        Directory.CreateDirectory(ConfigStore.AppDirectory);
        OpenShellPath(ConfigStore.AppDirectory);
        AddLog($"已打开数据目录：{ConfigStore.AppDirectory}");
    }

    private void OpenRuntimeLog_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ConfigStore.AppDirectory);
        if (!File.Exists(ConfigStore.RuntimeLogPath))
        {
            File.WriteAllText(
                ConfigStore.RuntimeLogPath,
                "OW Translator Lite runtime log\n",
                new UTF8Encoding(false));
        }

        OpenShellPath(ConfigStore.RuntimeLogPath);
        AddLog($"已打开日志：{ConfigStore.RuntimeLogPath}");
    }

    private void OpenDedupeLog_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ConfigStore.AppDirectory);
        if (!File.Exists(ConfigStore.DedupeLogPath))
        {
            File.WriteAllText(
                ConfigStore.DedupeLogPath,
                "OW Translator Lite dedupe debug log\n",
                new UTF8Encoding(false));
        }

        OpenShellPath(ConfigStore.DedupeLogPath);
        AddLog($"已打开去重日志：{ConfigStore.DedupeLogPath}");
    }

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        Directory.CreateDirectory(ConfigStore.AppDirectory);

        string diagnosticsPath = Path.Combine(
            ConfigStore.AppDirectory,
            $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        File.WriteAllText(
            diagnosticsPath,
            BuildDiagnosticsReport(),
            new UTF8Encoding(false));

        OpenShellPath(ConfigStore.AppDirectory);
        AddLog($"已导出诊断：{diagnosticsPath}");
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
        _overlay?.Hide();
        LogList.Items.Clear();
        _config.ResetUserData();
        _coordinator = CreateCoordinator();
        InvalidateOcrEngine();
        _activeRunSettingsKey = null;
        _pausedAt = null;
        _lastTranslationCompletedAt = null;
        _historyPeekOverlayUntil = null;
        _overlayVisibleForHistoryPeek = false;
        _overlayHiddenByIdle = false;
        _wasChatVisibleLastTick = false;
        _isAdjustingTranslationFrame = false;
        LoadSettingsToUi();
        ApplyFrameAdjustmentState();
        ApplyRunningState();
        AddLog("本机数据已清除，已恢复默认配置。");
    }

    private void SaveOverlayBounds(AppSettings settings)
    {
        if (_overlay is null)
        {
            return;
        }

        if (!IsFinite(_overlay.Left) ||
            !IsFinite(_overlay.Top) ||
            !IsFinite(_overlay.Width) ||
            !IsFinite(_overlay.Height))
        {
            return;
        }

        settings.OverlayLeft = _overlay.Left;
        settings.OverlayTop = _overlay.Top;
        settings.OverlayWidth = _overlay.Width;
        settings.OverlayHeight = _overlay.Height;
    }

    private async Task RunLoopAsync(int generation, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && IsActiveGeneration(generation))
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                IReadOnlyList<ParsedChatLine> newLines;
                await _ocrEngineGate.WaitAsync(cancellationToken);
                try
                {
                    IOcrEngine engine = GetOcrEngine();
                    newLines = await _coordinator.DetectNewLinesAsync(engine, cancellationToken);
                }
                finally
                {
                    _ocrEngineGate.Release();
                }

                if (!IsActiveGeneration(generation))
                {
                    break;
                }

                _recentChatLanguages.Record(_coordinator.LastVisibleChatLines);
                bool hasVisibleChat = _coordinator.HasVisibleChat;
                bool chatJustBecameVisible = hasVisibleChat && !_wasChatVisibleLastTick;
                _wasChatVisibleLastTick = hasVisibleChat;
                if (newLines.Count > 0)
                {
                    EnqueueTranslationLines(newLines, generation, cancellationToken);
                }
                else if (chatJustBecameVisible)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (IsActiveGeneration(generation))
                        {
                            ShowOverlayForHistoryPeek();
                        }
                    });
                }
                else if (_coordinator.ChatCycleJustReset)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (IsActiveGeneration(generation))
                        {
                            MaybeHideOverlayAfterIdle();
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
                    LatencyText.Text = $"{stopwatch.ElapsedMilliseconds} ms";
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

            int delay = Math.Clamp(_config.Settings.CaptureIntervalMs, 400, 3000);
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
        List<ParsedChatLine> dropped = [];
        lock (_translationQueueLock)
        {
            foreach (ParsedChatLine line in lines)
            {
                _translationQueue.Enqueue(line);
            }

            while (_translationQueue.Count > MaxTranslationQueueItems)
            {
                dropped.Add(_translationQueue.Dequeue());
            }
        }

        if (dropped.Count > 0)
        {
            _coordinator.ReleasePendingTranslations(dropped);
            Dispatcher.Invoke(() =>
            {
                if (IsActiveGeneration(generation))
                {
                    AddLog($"翻译队列过长，已跳过 {dropped.Count} 条较旧消息。");
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
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    IReadOnlyList<TranslationRecord> records = await _coordinator.TranslateAsync(batch, cancellationToken);
                    stopwatch.Stop();
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
                    _coordinator.ReleasePendingTranslations(batch);
                    break;
                }
                catch (Exception ex)
                {
                    _coordinator.ReleasePendingTranslations(batch);
                    Dispatcher.Invoke(() =>
                    {
                        if (IsActiveGeneration(generation))
                        {
                            AddLog($"翻译请求失败：{ex.Message}");
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

    private async Task<List<ParsedChatLine>> DequeueTranslationBatchAsync(CancellationToken cancellationToken)
    {
        List<ParsedChatLine> batch = [];
        lock (_translationQueueLock)
        {
            if (_translationQueue.Count == 0)
            {
                return batch;
            }

            batch.Add(_translationQueue.Dequeue());
        }

        try
        {
            await Task.Delay(TranslationBatchWindow, cancellationToken);
        }
        catch
        {
            _coordinator.ReleasePendingTranslations(batch);
            throw;
        }

        lock (_translationQueueLock)
        {
            while (batch.Count < MaxTranslationBatchSize && _translationQueue.Count > 0)
            {
                batch.Add(_translationQueue.Dequeue());
            }
        }

        return batch;
    }

    private void AddTranslationRecords(IReadOnlyList<TranslationRecord> records)
    {
        int addedCount = 0;
        foreach (TranslationRecord record in records)
        {
            if (IsDisplayDuplicate(record))
            {
                continue;
            }

            _records.Add(record);
            addedCount++;
            AddLog($"{record.Speaker}: {record.SourceText}  =>  {record.TranslatedText}");
        }

        if (addedCount == 0)
        {
            return;
        }

        TrimOverlayRecords();
        _lastTranslationCompletedAt = DateTime.Now;
        _historyPeekOverlayUntil = null;
        _overlayVisibleForHistoryPeek = false;
        _overlayHiddenByIdle = false;
        EnsureOverlay();
        _overlay?.Show();
        _overlay?.UpdateRecords(_records);
    }

    private bool IsDisplayDuplicate(TranslationRecord record)
    {
        string speaker = NormalizeSpeakerForCompare(record.Speaker);
        string source = NormalizeTextForCompare(record.SourceText);
        DateTime now = DateTime.Now;
        return _records.Any(existing =>
            now - existing.Timestamp <= DisplayDuplicateWindow &&
            OcrDedupeNormalizer.IsSpeakerMatch(NormalizeSpeakerForCompare(existing.Speaker), speaker) &&
            IsSimilarText(source, NormalizeTextForCompare(existing.SourceText)));
    }

    private IOcrEngine GetOcrEngine()
    {
        if (_currentOcrEngine is not null &&
            _currentOcrEngineName == _config.Settings.OcrEngine &&
            _currentOcrLanguage == _config.Settings.OcrLanguage)
        {
            return _currentOcrEngine;
        }

        DisposeCurrentOcrEngineUnlocked();
        _currentOcrEngineName = _config.Settings.OcrEngine;
        _currentOcrLanguage = _config.Settings.OcrLanguage;
        _currentOcrEngine = new OneOcrEngine();

        return _currentOcrEngine;
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
        _historyPeekOverlayUntil = null;
        _overlayVisibleForHistoryPeek = false;
        _overlayHiddenByIdle = false;
        _wasChatVisibleLastTick = false;
        _activeRunSettingsKey = CreateRunSettingsKey();
        StatusText.Text = "运行中";
        ApplyRunningState();
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
        ClearTranslationQueue();
        _coordinator.ClearPendingTranslations();
        ApplyRunningState();

        if (clearOverlay)
        {
            ClearOverlayRecords();
        }

        if (hideOverlay)
        {
            _pausedAt = DateTime.Now;
            _historyPeekOverlayUntil = null;
            _overlayVisibleForHistoryPeek = false;
            _overlayHiddenByIdle = false;
            _wasChatVisibleLastTick = false;
            _overlay?.Hide();
        }
    }

    private void EnsureOverlay()
    {
        if (_overlay is not null)
        {
            ApplyOverlaySettings();
            return;
        }

        _overlay = new OverlayWindow();
        _overlay.LocationChanged += Overlay_BoundsChanged;
        _overlay.SizeChanged += Overlay_BoundsChanged;
        _overlay.ReplySubmitted += Overlay_ReplySubmitted;
        _overlay.ReplyEditingStarted += Overlay_ReplyEditingStarted;
        _overlay.ReplyTargetLanguageChanged += Overlay_ReplyTargetLanguageChanged;
        _overlay.ReplyModeExited += Overlay_ReplyModeExited;
        ApplyOverlaySettings();
        if (_config.Settings.CaptureRegion is CaptureRegion region)
        {
            _overlay.MoveNear(region.ToRect());
        }
    }

    private void ApplyOverlaySettings()
    {
        if (_overlay is null)
        {
            return;
        }

        _isApplyingOverlaySettings = true;
        try
        {
            _overlay.ApplySettings(_config.Settings);
        }
        finally
        {
            _isApplyingOverlaySettings = false;
        }
    }

    private void Overlay_BoundsChanged(object? sender, EventArgs e)
    {
        if (_isApplyingOverlaySettings || _overlay is null)
        {
            return;
        }

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
        AppendRuntimeLog(line);
        LogList.Items.Add(line);
        while (LogList.Items.Count > MaxLogRecords)
        {
            LogList.Items.RemoveAt(0);
        }

        LogList.ScrollIntoView(line);
    }

    private string BuildDiagnosticsReport()
    {
        AppSettings settings = _config.Settings;
        StringBuilder builder = new();
        builder.AppendLine("OW Translator Lite Beta Diagnostics");
        builder.AppendLine($"GeneratedAt: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Version: {typeof(MainWindow).Assembly.GetName().Version}");
        builder.AppendLine($"OS: {Environment.OSVersion}");
        builder.AppendLine($".NET: {Environment.Version}");
        builder.AppendLine();
        builder.AppendLine("== Paths ==");
        builder.AppendLine($"AppDirectory: {ConfigStore.AppDirectory}");
        builder.AppendLine($"SettingsPath: {ConfigStore.SettingsPath}");
        builder.AppendLine($"RuntimeLogPath: {ConfigStore.RuntimeLogPath}");
        builder.AppendLine($"CrashLogPath: {ConfigStore.CrashLogPath}");
        builder.AppendLine($"DedupeLogPath: {ConfigStore.DedupeLogPath}");
        builder.AppendLine();
        builder.AppendLine("== Settings ==");
        builder.AppendLine($"OcrEngine: {settings.OcrEngine}");
        builder.AppendLine($"OcrLanguage: {settings.OcrLanguage}");
        builder.AppendLine($"TranslationProvider: {settings.TranslationProvider}");
        builder.AppendLine($"ApiUrl: {settings.ApiUrl}");
        builder.AppendLine($"ApiKeyConfigured: {!string.IsNullOrWhiteSpace(settings.ApiKey)}");
        builder.AppendLine("ApiKey: [redacted]");
        builder.AppendLine($"Model: {settings.Model}");
        builder.AppendLine($"ReplyTargetLanguage: {settings.ReplyTargetLanguage}");
        builder.AppendLine($"EnableReplyHotkey: {settings.EnableReplyHotkey}");
        builder.AppendLine($"ReplyHotkey: {settings.ReplyHotkey}");
        builder.AppendLine($"CaptureIntervalMs: {settings.CaptureIntervalMs}");
        builder.AppendLine($"RequestTimeoutSeconds: {settings.RequestTimeoutSeconds}");
        builder.AppendLine($"OverlayOpacity: {settings.OverlayOpacity:0.###}");
        builder.AppendLine($"OverlayFontSize: {settings.OverlayFontSize:0.###}");
        builder.AppendLine($"OverlayClickThrough: {settings.OverlayClickThrough}");
        builder.AppendLine($"EnableDedupeDebugLog: {settings.EnableDedupeDebugLog}");
        builder.AppendLine($"OverlayBounds: {FormatBounds(settings)}");
        builder.AppendLine($"CaptureRegion: {FormatRegion(settings.CaptureRegion)}");
        builder.AppendLine();
        builder.AppendLine("== Current UI Log ==");
        foreach (object item in LogList.Items.Cast<object>().TakeLast(80))
        {
            builder.AppendLine(item.ToString());
        }

        AppendFileTail(builder, ConfigStore.RuntimeLogPath, "Runtime Log Tail", 120);
        AppendFileTail(builder, ConfigStore.CrashLogPath, "Crash Log Tail", 120);
        AppendFileTail(builder, ConfigStore.DedupeLogPath, "Dedupe Debug Log Tail", 200);

        return builder.ToString();
    }

    private static string FormatBounds(AppSettings settings)
    {
        if (settings.OverlayLeft is not double left ||
            settings.OverlayTop is not double top ||
            settings.OverlayWidth is not double width ||
            settings.OverlayHeight is not double height)
        {
            return "not saved";
        }

        return $"{left:0.##},{top:0.##} {width:0.##}x{height:0.##}";
    }

    private static string FormatRegion(CaptureRegion? region) =>
        region is null
            ? "not selected"
            : $"{region.Left:0.##},{region.Top:0.##} {region.Width:0.##}x{region.Height:0.##}";

    private static void AppendFileTail(StringBuilder builder, string path, string title, int maxLines)
    {
        builder.AppendLine();
        builder.AppendLine($"== {title} ==");
        if (!File.Exists(path))
        {
            builder.AppendLine("not found");
            return;
        }

        try
        {
            foreach (string line in File.ReadLines(path, Encoding.UTF8).TakeLast(maxLines))
            {
                builder.AppendLine(line);
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine($"unavailable: {ex.Message}");
        }
    }

    private static void AppendRuntimeLog(string line)
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.AppDirectory);
            File.AppendAllText(
                ConfigStore.RuntimeLogPath,
                line + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch
        {
            // Runtime logging is diagnostic-only and must not interrupt translation.
        }
    }

    private void AppendDedupeLog(string message)
    {
        if (!_config.Settings.EnableDedupeDebugLog)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(ConfigStore.AppDirectory);
            File.AppendAllText(
                ConfigStore.DedupeLogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
            // Dedupe debug logging is optional and must not affect OCR or translation.
        }
    }

    private async void Overlay_ReplySubmitted(object? sender, ReplySubmittedEventArgs e)
    {
        if (_replyTranslationCts is not null)
        {
            _overlay?.SetReplyStatus("上一句还在翻译");
            return;
        }

        string targetLanguage = e.SelectedLanguage == "auto" ? ResolveReplyTargetLanguage() : e.SelectedLanguage;
        _overlay?.SetReplyTargetLanguage(_config.Settings.ReplyTargetLanguage, targetLanguage);
        _overlay?.SetReplyStatus("翻译中...");
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
                _overlay?.SetReplyStatus("翻译为空");
                return;
            }

            System.Windows.Clipboard.SetText(translated);
            _overlay?.ClearReplyInput();
            _overlay?.SetReplyStatus($"已复制：{LimitReplyStatus(translated)}");
            _isReplyModeActive = false;
            AddLog($"回话已复制：{translated}");
        }
        catch (OperationCanceledException)
        {
            _overlay?.SetReplyStatus("已取消");
        }
        catch (Exception ex)
        {
            _overlay?.SetReplyStatus($"失败：{LimitReplyStatus(ex.Message)}");
            AddLog($"回话翻译失败：{ex.Message}");
        }
        finally
        {
            _replyTranslationCts?.Dispose();
            _replyTranslationCts = null;
            _overlay?.SetReplyTargetLanguage(_config.Settings.ReplyTargetLanguage, ResolveReplyTargetLanguage());
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
        _historyPeekOverlayUntil = null;
        _overlayVisibleForHistoryPeek = false;
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
        _historyPeekOverlayUntil = null;
        _overlayVisibleForHistoryPeek = false;
        _overlayHiddenByIdle = false;
        _overlay?.EnterReplyMode(_config.Settings.ReplyTargetLanguage, ResolveReplyTargetLanguage());
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
        _overlay?.ExitReplyMode();
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
        if (!_isReplyModeActive || _overlay is null)
        {
            return;
        }

        _overlay.SetReplyTargetLanguage(_config.Settings.ReplyTargetLanguage, ResolveReplyTargetLanguage());
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
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
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
        _historyPeekOverlayUntil = null;
        _overlayVisibleForHistoryPeek = false;
        _overlayHiddenByIdle = false;
        _overlay?.UpdateRecords(_records);
    }

    private void ClearTranslationQueue()
    {
        lock (_translationQueueLock)
        {
            _translationQueue.Clear();
        }
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
        if (!_isRunning || _overlay is null)
        {
            return;
        }

        if (_overlayVisibleForHistoryPeek)
        {
            if (_historyPeekOverlayUntil is DateTime until && DateTime.Now >= until)
            {
                _overlay.Hide();
                _overlayVisibleForHistoryPeek = false;
                _historyPeekOverlayUntil = null;
                _overlayHiddenByIdle = true;
            }

            return;
        }

        if (_overlayHiddenByIdle || _coordinator.HasVisibleChat)
        {
            return;
        }

        if (_lastTranslationCompletedAt is not DateTime completedAt ||
            DateTime.Now - completedAt < OverlayIdleHideDelay)
        {
            return;
        }

        _overlay.Hide();
        _overlayHiddenByIdle = true;
    }

    private void ShowOverlayForHistoryPeek()
    {
        if (!_isRunning || _records.Count == 0)
        {
            return;
        }

        EnsureOverlay();
        if (_overlay is null || _overlay.IsVisible)
        {
            return;
        }

        _historyPeekOverlayUntil = DateTime.Now + OverlayHistoryPeekDuration;
        _overlayVisibleForHistoryPeek = true;
        _overlayHiddenByIdle = false;
        _overlay.UpdateRecords(_records);
        _overlay.Show();
        AppendDedupeLog("history-peek show overlay for visible chat without new messages");
    }

    private void InvalidateOcrEngine()
    {
        DisposeCurrentOcrEngine();
        _currentOcrEngineName = null;
        _currentOcrLanguage = null;
    }

    private void DisposeCurrentOcrEngine()
    {
        _ocrEngineGate.Wait();
        try
        {
            DisposeCurrentOcrEngineUnlocked();
        }
        finally
        {
            _ocrEngineGate.Release();
        }
    }

    private void DisposeCurrentOcrEngineUnlocked()
    {
        if (_currentOcrEngine is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _currentOcrEngine = null;
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

    private static string NormalizeSpeakerForCompare(string value)
        => OcrDedupeNormalizer.NormalizeSpeaker(value);

    private static string NormalizeTextForCompare(string value)
        => OcrDedupeNormalizer.NormalizeText(value);

    private static bool IsSimilarText(string left, string right)
    {
        return OcrDedupeNormalizer.IsSimilarText(left, right);
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

}
