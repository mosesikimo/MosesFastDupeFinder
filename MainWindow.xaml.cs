using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.WindowsAPICodePack.Shell;

namespace FastDupeFinder
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<DupeFileItem> Duplicates { get; set; } = new();

        private List<DupeFileItem> _cachedScanResults = new();
        private bool _isScanning = false;
        private Guid _currentPreviewId;

        private CancellationTokenSource? _cancellationTokenSource;

        private ConcurrentDictionary<string, CachedVideoData> _fingerprintCache = new();

        private readonly Channel<DupeFileItem> _taskQueue = Channel.CreateUnbounded<DupeFileItem>();
        private int _taskPendingCount = 0;

        private string _appVersion = "1.0.0";
        private string Loc(string key, string def) => I18nManager.Instance.GetString(key, def);

        private static readonly char[] _pathSplitChars = { ';', ',' };

        public MainWindow()
        {
            I18nManager.Instance.Init();
            InitializeComponent();
            this.WindowState = WindowState.Maximized;
            
            FileListView.ItemsSource = Duplicates;

            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;
            I18nManager.Instance.LanguageChanged += OnLanguageChanged;
        }

        private void OnLanguageChanged()
        {
            this.Title = Loc("WindowTitle", "渡川 Moses - 快速重複影片檢索工具") + $" (v{_appVersion})";
            if (!_isScanning && _taskPendingCount == 0) StatusText.Text = Loc("StatusReady", "✅ 系統就緒，準備掃描。");
            UpdateGlobalUIState();

            if (FileListView.ContextMenu != null)
            {
                ((MenuItem)FileListView.ContextMenu.Items[0]).Header = Loc("MenuRepair", "🛠️ 嘗試斷尾修復 (容許損失 < 5%)");
                ((MenuItem)FileListView.ContextMenu.Items[1]).Header = Loc("MenuUpgrade", "✨ 升級為現代 MP4 格式 (極速換殼/重新編碼)");
                ((MenuItem)FileListView.ContextMenu.Items[3]).Header = Loc("MenuOpenFolder", "📂 開啟檔案位置");
                ((MenuItem)FileListView.ContextMenu.Items[4]).Header = Loc("MenuDelete", "🗑️ 刪除選取的檔案");
            }
            FileListView.Items.Refresh();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (version != null) _appVersion = $"{version.Major}.{version.Minor}.{version.Build}";
            this.Title = Loc("WindowTitle", "渡川 Moses - 快速重複影片檢索工具") + $" (v{_appVersion})";

            LoadSettings();
            LoadFingerprintCache();
            StatusText.Text = Loc("StatusReady", "✅ 系統就緒，準備掃描。");
            
            _ = Task.Run(BackgroundWorkerLoopAsync);

            var ctxMenu = new ContextMenu();
            var repairMenu = new MenuItem { Header = Loc("MenuRepair", "🛠️ 嘗試斷尾修復 (容許損失 < 5%)") };
            repairMenu.Click += RepairSelected_Click;
            var upgradeMenu = new MenuItem { Header = Loc("MenuUpgrade", "✨ 升級為現代 MP4 格式 (極速換殼/重新編碼)") };
            upgradeMenu.Click += UpgradeSelected_Click;
            var deleteMenu = new MenuItem { Header = Loc("MenuDelete", "🗑️ 刪除選取的檔案") };
            deleteMenu.Click += DeleteSelected_Click;
            var openFolderMenu = new MenuItem { Header = Loc("MenuOpenFolder", "📂 開啟檔案位置") };
            openFolderMenu.Click += OpenDirectory_Click;

            ctxMenu.Items.Add(repairMenu);
            ctxMenu.Items.Add(upgradeMenu);
            ctxMenu.Items.Add(new Separator());
            ctxMenu.Items.Add(openFolderMenu);
            ctxMenu.Items.Add(deleteMenu);
            FileListView.ContextMenu = ctxMenu;

            UpdateGlobalUIState();
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveSettings();
            SaveFingerprintCache();
            I18nManager.Instance.ExportTemplate(); 
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            string aboutText = Loc("AboutContent", I18nManager.DefaultAboutContent);
            aboutText = $"[FastDupeFinder Version: {_appVersion}]\n\n" + aboutText;

            var aboutWindow = new Window
            {
                Title = Loc("AboutTitle", "關於 FastDupeFinder & 作者"), Width = 900, Height = 700, 
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(248, 249, 250))
            };

            aboutWindow.Content = new TextBox
            {
                Text = aboutText, Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Microsoft JhengHei"), FontSize = 14, IsReadOnly = true,
                BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            aboutWindow.ShowDialog();
        }

        private void LoadFingerprintCache()
        {
            try
            {
                // 💡 推進到 v9，強制作廢舊有缺失全畫面特徵的快取
                string cachePath = Path.Combine(AppContext.BaseDirectory, "fingerprint_cache_v9.json");
                if (File.Exists(cachePath))
                {
                    string json = File.ReadAllText(cachePath, Encoding.UTF8);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, CachedVideoData>>(json);
                    if (dict != null) _fingerprintCache = new ConcurrentDictionary<string, CachedVideoData>(dict);
                }
            }
            catch { }
        }

        private void SaveFingerprintCache()
        {
            try
            {
                string cachePath = Path.Combine(AppContext.BaseDirectory, "fingerprint_cache_v9.json");
                string json = JsonSerializer.Serialize(_fingerprintCache);
                File.WriteAllText(cachePath, json, Encoding.UTF8);
            }
            catch { }
        }

        private void UpdateGlobalUIState()
        {
            bool isQueueRunning = _taskPendingCount > 0;
            bool isIdle = !_isScanning && !isQueueRunning; 

            if (this.FindName("PathTextBox") is TextBox pt) pt.IsEnabled = isIdle;
            if (this.FindName("MinSizeTextBox") is TextBox mst) mst.IsEnabled = isIdle;
            if (this.FindName("SubDirCheckBox") is CheckBox sdc) sdc.IsEnabled = isIdle;
            if (this.FindName("MinLengthTextBox") is TextBox minLenTb) minLenTb.IsEnabled = isIdle;
            if (this.FindName("ExcludePathTextBox") is TextBox excludeTb) excludeTb.IsEnabled = isIdle;
            if (this.FindName("DeleteBtn") is Button delBtn) delBtn.IsEnabled = isIdle;

            if (this.FindName("ScanBtn") is Button scanBtn) {
                if (_isScanning) {
                    scanBtn.IsEnabled = true; 
                    scanBtn.Content = Loc("BtnStopScan", "🛑 停止掃描");
                    try { scanBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFD6D6")); } catch { }
                } else if (isQueueRunning) {
                    scanBtn.IsEnabled = false; 
                    scanBtn.Content = Loc("BtnScan", "🔍 開始掃描");
                    try { scanBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFEEEEEE")); } catch { }
                } else {
                    scanBtn.IsEnabled = true;
                    scanBtn.Content = _cachedScanResults.Count > 0 ? Loc("BtnRescan", "🔍 重新掃描") : Loc("BtnScan", "🔍 開始掃描");
                    try { scanBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFEBF2")); } catch { }
                }
            }

            if (FileListView.SelectedItem is DupeFileItem selected) UpdatePreviewButtonsState(selected);
            
            if (FileListView.ContextMenu != null) {
                foreach(var item in FileListView.ContextMenu.Items) {
                    if (item is MenuItem mi) mi.IsEnabled = isIdle;
                }
            }
        }

        private void UpdatePreviewButtonsState(DupeFileItem? targetItem)
        {
            if (targetItem == null)
            {
                if (this.FindName("UpgradeBtn") is Button uBtn) uBtn.Visibility = Visibility.Collapsed;
                if (this.FindName("RepairBtn") is Button rBtn) rBtn.Visibility = Visibility.Collapsed;
                return;
            }

            if (targetItem.IsFailed)
            {
                if (targetItem.IsFormatUpgradeNeeded) {
                    if (this.FindName("UpgradeBtn") is Button ub) { 
                        ub.Visibility = Visibility.Visible; 
                        ub.IsEnabled = !targetItem.IsUpgrading; 
                    }
                    if (this.FindName("RepairBtn") is Button rb) rb.Visibility = Visibility.Collapsed;
                } else {
                    if (this.FindName("UpgradeBtn") is Button ub) ub.Visibility = Visibility.Collapsed;
                    if (this.FindName("RepairBtn") is Button rb) { 
                        rb.Visibility = Visibility.Visible; 
                        rb.IsEnabled = !targetItem.IsRepairing; 
                    }
                }
            }
            else
            {
                if (this.FindName("UpgradeBtn") is Button ub) ub.Visibility = Visibility.Collapsed;
                if (this.FindName("RepairBtn") is Button rb) rb.Visibility = Visibility.Collapsed;
            }
        }

        private void LoadSettings()
        {
            try
            {
                string configPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath, Encoding.UTF8);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);

                    if (settings != null)
                    {
                        if (this.FindName("PathTextBox") is TextBox pt) pt.Text = settings.TargetPaths;
                        if (this.FindName("ExcludePathTextBox") is TextBox et) et.Text = settings.ExcludePaths;
                        if (this.FindName("SubDirCheckBox") is CheckBox sc) sc.IsChecked = settings.IncludeSubDir;
                        if (this.FindName("MinSizeTextBox") is TextBox mt) mt.Text = settings.MinSize;
                        if (this.FindName("MinLengthTextBox") is TextBox mlt) mlt.Text = settings.MinLength;
                        return;
                    }
                }
            }
            catch { }
            SetupDefaultSearchPath();
        }

        private void SaveSettings()
        {
            try
            {
                var settings = new AppSettings
                {
                    TargetPaths = (this.FindName("PathTextBox") as TextBox)?.Text?.Trim() ?? "",
                    ExcludePaths = (this.FindName("ExcludePathTextBox") as TextBox)?.Text?.Trim() ?? "",
                    IncludeSubDir = (this.FindName("SubDirCheckBox") as CheckBox)?.IsChecked ?? true,
                    MinSize = (this.FindName("MinSizeTextBox") as TextBox)?.Text?.Trim() ?? "100",
                    MinLength = (this.FindName("MinLengthTextBox") as TextBox)?.Text?.Trim() ?? "3"
                };

                string configPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
                File.WriteAllText(configPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            }
            catch { }
        }

        private void SetupDefaultSearchPath()
        {
            List<string> availablePaths = new List<string>();
            if (Directory.Exists("D:\\")) availablePaths.Add("D:\\");
            if (Directory.Exists("E:\\")) availablePaths.Add("E:\\");
            if (this.FindName("PathTextBox") is TextBox pt) pt.Text = availablePaths.Count > 0 ? string.Join("; ", availablePaths) : (Directory.Exists("C:\\") ? "C:\\" : "");
        }

        private async Task BackgroundWorkerLoopAsync()
        {
            await foreach (var item in _taskQueue.Reader.ReadAllAsync())
            {
                int pending = Interlocked.Decrement(ref _taskPendingCount);
                
                await Application.Current.Dispatcher.InvokeAsync(() => {
                    string actionStr = item.IsUpgrading ? Loc("StatusUpgradingQueue", "✨ 正在升級: {0}... (佇列剩餘 {1} 筆)") : Loc("StatusRepairingQueue", "🛠️ 正在修復: {0}... (佇列剩餘 {1} 筆)");
                    StatusText.Text = string.Format(actionStr, Path.GetFileName(item.FilePath), pending);
                });

                if (item.IsUpgrading) await ProcessUpgradeItemAsync(item);
                else if (item.IsRepairing) await ProcessRepairItemAsync(item);

                await Application.Current.Dispatcher.InvokeAsync(() => {
                    item.IsRepairing = false;
                    item.IsUpgrading = false;

                    if (FileListView.SelectedItem is DupeFileItem selected && selected == item) UpdatePreviewButtonsState(selected);
                    
                    if (pending == 0)
                    {
                        StatusText.Text = Loc("StatusQueueDone", "✅ 所有背景佇列已全數處理完畢！");
                        UpdateGlobalUIState();
                    }
                });
            }
        }

        private async Task<bool> ApplyRecoveredFileAsync(DupeFileItem item, string tempPath, string finalPath, string successMsg, double? maxLossPercentage = null)
        {
            long fileSize = 0;
            long fileTicks = 0;
            try 
            { 
                var fi = new FileInfo(tempPath);
                fileSize = fi.Length; 
                fileTicks = fi.LastWriteTimeUtc.Ticks;
            } 
            catch { return false; }

            var inspect = await FileHelper.InspectFileAsync(tempPath, fileSize, fileTicks, _fingerprintCache, null, CancellationToken.None);
            var newMeta = inspect?.Metadata;

            if (newMeta != null && newMeta.Duration.TotalSeconds > 0)
            {
                if (maxLossPercentage.HasValue)
                {
                    double origSec = item.Duration.TotalSeconds;
                    double repSec = newMeta.Duration.TotalSeconds;
                    double lossPercent = ((origSec - repSec) / origSec) * 100.0;

                    if (lossPercent > maxLossPercentage.Value || repSec <= 0)
                    {
                        try { File.Delete(tempPath); } catch { }
                        item.ErrorMessage = string.Format(Loc("Microsoft JhengHei", "放棄修復 (尾部缺少達 {0:F1}%)"), lossPercent);
                        return false;
                    }
                    successMsg = string.Format(successMsg, lossPercent);
                }

                try
                {
                    if (!item.FilePath.Equals(finalPath, StringComparison.OrdinalIgnoreCase)) File.Delete(item.FilePath);
                    File.Move(tempPath, finalPath);

                    item.FilePath = finalPath;
                    item.IsFailed = false;
                    item.IsFormatUpgradeNeeded = false;
                    item.Size = newMeta.Size;
                    item.Duration = newMeta.Duration;
                    item.MatchDetail = successMsg;
                    item.RowBackgroundColor = "#E8FFE8";
                    return true;
                }
                catch { item.ErrorMessage = Loc("StatusRepairFailLock", "❌ 檔案被鎖定或無法覆蓋。"); }
            }
            else
            {
                try { File.Delete(tempPath); } catch { }
                item.ErrorMessage = Loc("ErrUpgradeMeta", "處理失敗 (無法解析輸出檔案)");
            }
            return false;
        }

        private async Task ProcessUpgradeItemAsync(DupeFileItem item)
        {
            await Application.Current.Dispatcher.InvokeAsync(() => item.ErrorMessage = Loc("StatusUpgradingNow", "✨ 正在執行升級轉換..."));
            string dir = Path.GetDirectoryName(item.FilePath) ?? "";
            string name = Path.GetFileNameWithoutExtension(item.FilePath);
            string tempOutPath = Path.Combine(dir, $"{name}_upgrading.mp4");

            bool success = await FFmpegHelper.RunCommandAsync($"-err_detect ignore_err -i \"{item.FilePath}\" -c copy -y \"{tempOutPath}\"", CancellationToken.None);
            bool isValid = success && File.Exists(tempOutPath) && new FileInfo(tempOutPath).Length > 1024;

            if (!isValid)
            {
                if (File.Exists(tempOutPath)) try { File.Delete(tempOutPath); } catch {}
                await Application.Current.Dispatcher.InvokeAsync(() => StatusText.Text = string.Format(Loc("StatusReencoding", "🔄 換殼失敗，正在進行重新編碼: {0}..."), Path.GetFileName(item.FilePath)));

                string reencodeArgs = $"-err_detect ignore_err -i \"{item.FilePath}\" -c:v libx264 -preset veryfast -crf 23 -c:a aac -b:a 128k -y \"{tempOutPath}\"";
                success = await FFmpegHelper.RunCommandAsync(reencodeArgs, CancellationToken.None);
                isValid = success && File.Exists(tempOutPath) && new FileInfo(tempOutPath).Length > 1024;
            }

            if (isValid)
            {
                string finalPath = Path.Combine(dir, $"{name}.mp4");
                if (File.Exists(finalPath) && !finalPath.Equals(item.FilePath, StringComparison.OrdinalIgnoreCase))
                    finalPath = Path.Combine(dir, $"{name}_upgraded.mp4");

                await ApplyRecoveredFileAsync(item, tempOutPath, finalPath, Loc("StatusUpgradeDone", "✨ 成功升級為現代 MP4 格式"));
            }
            else item.ErrorMessage = Loc("ErrUpgradeFailed", "升級失敗 (檔案嚴重損毀或不支援)");
        }

        private async Task ProcessRepairItemAsync(DupeFileItem item)
        {
            await Application.Current.Dispatcher.InvokeAsync(() => item.ErrorMessage = Loc("StatusRepairingNow", "🛠️ 正在執行修復..."));
            string dir = Path.GetDirectoryName(item.FilePath) ?? "";
            string name = Path.GetFileNameWithoutExtension(item.FilePath);
            string ext = Path.GetExtension(item.FilePath);
            string tempPath = Path.Combine(dir, $"{name}_repaired{ext}");

            bool success = await FFmpegHelper.RunCommandAsync($"-err_detect ignore_err -i \"{item.FilePath}\" -c copy -y \"{tempPath}\"", CancellationToken.None);
            
            if (success && File.Exists(tempPath) && new FileInfo(tempPath).Length > 1024)
            {
                await ApplyRecoveredFileAsync(item, tempPath, item.FilePath, Loc("StatusRepairDone", "🛠️ 成功切除尾部 {0:F1}% (修復完成)"), 5.0);
            }
            else item.ErrorMessage = Loc("ErrRepairLocked", "修復失敗 (檔案嚴重損毀或被鎖定)");
        }

        private int EnqueueTasks(List<DupeFileItem> items, bool isUpgrade)
        {
            int queuedCount = 0;
            foreach (var item in items)
            {
                if (item.IsRepairing || item.IsUpgrading) continue;
                
                if (isUpgrade) item.IsUpgrading = true;
                else item.IsRepairing = true;

                item.ErrorMessage = Loc("StatusTaskQueued", "⏳ 已加入背景佇列...");
                _taskQueue.Writer.TryWrite(item);
                Interlocked.Increment(ref _taskPendingCount);
                queuedCount++;
            }
            if (FileListView.SelectedItem is DupeFileItem selected) UpdatePreviewButtonsState(selected);
            return queuedCount;
        }

        private void HandleBatchAction(bool isUpgrade)
        {
            var selectedItems = FileListView.SelectedItems.Cast<DupeFileItem>()
                .Where(x => isUpgrade ? x.IsFormatUpgradeNeeded : (x.IsFailed && !x.IsFormatUpgradeNeeded)).ToList();

            if (!selectedItems.Any())
            {
                if (!isUpgrade) MessageBox.Show(Loc("MsgSelectBadFile", "請選擇標示為「淺紅色」的壞檔進行修復！\n(正常的檔案不需要修復)"), Loc("TitleHint", "提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int queuedCount = EnqueueTasks(selectedItems, isUpgrade);
            if (queuedCount > 0)
            {
                UpdateGlobalUIState();
                StatusText.Text = string.Format(Loc("StatusAddedToQueue", "📥 已將 {0} 個檔案加入背景佇列。"), queuedCount);
            }
        }

        private void HandleSingleAction(bool isUpgrade)
        {
            if (FileListView.SelectedItem is DupeFileItem item && !item.IsUpgrading && !item.IsRepairing)
            {
                bool valid = isUpgrade ? item.IsFormatUpgradeNeeded : (item.IsFailed && !item.IsFormatUpgradeNeeded);
                if (!valid || !File.Exists(item.FilePath)) return;

                if (isUpgrade) { if (this.FindName("UpgradeBtn") is Button u) u.IsEnabled = false; }
                else { if (this.FindName("RepairBtn") is Button r) r.IsEnabled = false; }

                EnqueueTasks(new List<DupeFileItem> { item }, isUpgrade);
                UpdateGlobalUIState();
                StatusText.Text = Loc("StatusAddedToQueueSingle", "📥 檔案已加入背景佇列。");
            }
        }

        private void UpgradeSelected_Click(object sender, RoutedEventArgs e) => HandleBatchAction(isUpgrade: true);
        private void RepairSelected_Click(object sender, RoutedEventArgs e) => HandleBatchAction(isUpgrade: false);
        private void UpgradeBtn_Click(object sender, RoutedEventArgs e) => HandleSingleAction(isUpgrade: true);
        private void RepairBtn_Click(object sender, RoutedEventArgs e) => HandleSingleAction(isUpgrade: false);

        private void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isScanning)
            {
                if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
                {
                    _cancellationTokenSource.Cancel();
                    StatusText.Text = Loc("StatusStopping", "⏳ 正在停止管線，請稍候...");
                    if (this.FindName("ScanBtn") is Button scanBtn) scanBtn.IsEnabled = false;
                }
                return;
            }

            string minLenText = (this.FindName("MinLengthTextBox") as TextBox)?.Text?.Trim() ?? "3";
            if (!int.TryParse(minLenText, out int minLength) || minLength < 3)
            {
                MessageBox.Show(Loc("MsgMinLenErr", "「最短長度」必須設定為至少 3 秒！\n這能確保不會比對到無意義的零碎短片。"), Loc("TitleSettingErr", "設定錯誤"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ExecuteScanCore();
        }

        private async void ExecuteScanCore()
        {
            string pathText = (this.FindName("PathTextBox") as TextBox)?.Text ?? "";
            if (_isScanning || string.IsNullOrWhiteSpace(pathText)) return;

            SaveSettings();

            _isScanning = true;
            _cancellationTokenSource = new CancellationTokenSource();
            UpdateGlobalUIState(); 

            FileListView.ItemsSource = null;
            
            Duplicates = new ObservableCollection<DupeFileItem>();
            _cachedScanResults = new List<DupeFileItem>();
            
            ReleasePreviewMemory();
            StatusText.Text = Loc("StatusScanning", "⏳ 啟動雙層限流管線掃描...");

            string searchPath = pathText.Trim();
            string excludePath = (this.FindName("ExcludePathTextBox") as TextBox)?.Text?.Trim() ?? "";
            bool includeSub = (this.FindName("SubDirCheckBox") as CheckBox)?.IsChecked == true;
            string minSizeStr = (this.FindName("MinSizeTextBox") as TextBox)?.Text?.Trim() ?? "100";
            string minLengthStr = (this.FindName("MinLengthTextBox") as TextBox)?.Text?.Trim() ?? "3";

            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                var progress = new Progress<string>(message => { StatusText.Text = message; });
                int ffmpegTimeoutSec = 5; 
                int ffmpegTimeoutMs = ffmpegTimeoutSec * 1000;
                
                _cachedScanResults = await PerformScanAsync(
                    searchPath, excludePath, includeSub, minSizeStr, minLengthStr,
                    progress, _cancellationTokenSource.Token, ffmpegTimeoutMs);
                    
                sw.Stop();
                ApplyFilter(sw.Elapsed); 
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                if (!StatusText.Text.Contains(Loc("ProgDryRunAborted", "⏹️ 已於目錄掃描後終止")))
                {
                    StatusText.Text = Loc("StatusAborted", "⏹️ 掃描已由使用者手動終止。");
                }
                ApplyFilter(sw.Elapsed); 
            }
            catch (Exception ex)
            {
                sw.Stop();
                MessageBox.Show(ex.Message, Loc("TitleBusyErr", "系統忙碌中 / 錯誤"), MessageBoxButton.OK, MessageBoxImage.Information);
                StatusText.Text = Loc("StatusScanErr", "❌ 掃描發生錯誤");
            }
            finally
            {
                _isScanning = false;
                if (_cancellationTokenSource != null)
                {
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = null;
                }

                UpdateGlobalUIState(); 
                _ = Task.Run(() => SaveFingerprintCache());
            }
        }

        private void ApplyFilter(TimeSpan? elapsed = null)
        {
            FileListView.ItemsSource = null;
            ReleasePreviewMemory();

            string timeMsg = elapsed.HasValue ? string.Format(Loc("StatusTime", " (耗時: {0}分{1}秒)"), (int)elapsed.Value.TotalMinutes, elapsed.Value.Seconds) : "";

            if (_cachedScanResults == null || _cachedScanResults.Count == 0)
            {
                if (!StatusText.Text.Contains(Loc("TextAborted", "手動終止")) && !StatusText.Text.Contains("⏹️ 已於目錄掃描後終止"))
                    StatusText.Text = Loc("StatusNoResult", "ℹ️ 沒有找到任何重複或損壞的檔案。") + timeMsg;
                else
                    StatusText.Text += timeMsg; 
                
                Duplicates = new ObservableCollection<DupeFileItem>(); 
                FileListView.ItemsSource = Duplicates; 
                return;
            }

            var tempList = new List<DupeFileItem>(_cachedScanResults.Count);

            var validGroups = _cachedScanResults.Where(x => !x.IsFailed).GroupBy(x => x.OriginalGroupId).Where(g => g.Count() >= 2).ToList();
            bool isColorOne = true;
            string color1 = "#FFFFFF", color2 = "#EBF5FF";
            int validCount = 0;

            foreach (var g in validGroups)
            {
                string currentColor = isColorOne ? color1 : color2;
                foreach (var item in g)
                {
                    item.RowBackgroundColor = currentColor;
                    tempList.Add(item);
                    validCount++;
                }
                isColorOne = !isColorOne;
            }

            var failedFiles = _cachedScanResults.Where(x => x.IsFailed).OrderBy(x => x.FilePath).ToList();
            if (failedFiles.Any())
            {
                foreach (var item in failedFiles)
                {
                    item.RowBackgroundColor = item.IsFormatUpgradeNeeded ? "#FFF3E0" : "#FFF0F0"; 
                    tempList.Add(item);
                }
            }

            Duplicates = new ObservableCollection<DupeFileItem>(tempList);
            FileListView.ItemsSource = Duplicates;

            if (validCount > 0 || failedFiles.Count > 0)
            {
                string statusMsg = Loc("StatusRefreshed", "✅ 畫面已刷新！");
                if (validCount > 0) statusMsg += string.Format(Loc("StatusDupeFound", " 發現 {0} 個重複檔案。"), validCount);
                if (failedFiles.Count > 0) statusMsg += string.Format(Loc("StatusBadFound", " ⚠️ 並列出 {0} 個無法解析/下載中檔案供您清理。"), failedFiles.Count);
                
                statusMsg += timeMsg; 
                StatusText.Text = statusMsg;
            }
        }

        private async Task<List<DupeFileItem>> PerformScanAsync(
            string pathStr, string excludePathStr, bool includeSubDir, string minSizeStr, string minLengthStr,
            IProgress<string> progress, CancellationToken ct, int ffmpegTimeoutMs)
        {
            var cfg = new PipelineConfig();
            progress.Report(Loc("ProgStart", "⚡ 啟動全記憶體非同步管線 (配置: ") + cfg + ")");

            var uiStopwatch = Stopwatch.StartNew();

            var paths = pathStr.Split(_pathSplitChars, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(Directory.Exists).ToList();
            var excludes = excludePathStr.Split(_pathSplitChars, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
            if (paths.Count == 0) return new List<DupeFileItem>();

            if (!long.TryParse(minSizeStr, out long sizeMB)) sizeMB = 100;
            if (!int.TryParse(minLengthStr, out int minLengthSecInput)) minLengthSecInput = 3;

            long minSizeBytes = sizeMB * 1024 * 1024;
            double minLengthSec = minLengthSecInput;

            var finalResultsBag = new ConcurrentBag<DupeFileItem>();
            var failedFilesBag = new ConcurrentBag<DupeFileItem>(); 

            int statsFound = 0, statsMetaProcessed = 0, statsFfmpegProcessed = 0;
            int statsCacheHits = 0, statsExtractFailed = 0, statsSkippedLocked = 0, statsSkippedShort = 0; 

            Action<int, int, int, int, int, int, int> ReportProg = (fnd, meta, ffmpeg, cache, fail, lck, shrt) => {
                if (uiStopwatch.ElapsedMilliseconds < 3000) return; 
                
                string failMsg = fail > 0 ? Loc("ProgFail", " | ⚠️ ") + $"{fail} " + Loc("ProgTimeout", "慢速檔或損壞影片") : "";
                string lockMsg = lck > 0 ? Loc("ProgLock", " | 🔒 ") + $"{lck} " + Loc("ProgDl", "被系統鎖定") : "";
                string shortMsg = shrt > 0 ? Loc("ProgShort", " | ⏭️ ") + $"{shrt} " + Loc("ProgSkipShort", "太短略過") : "";
                progress.Report(string.Format(Loc("ProgStats", "搜尋: {0} 檔 | 解析: {1} 檔 | 獲取: {2} 特徵 | 快取: {3} 次"), fnd, meta, ffmpeg, cache) + failMsg + lockMsg + shortMsg);
                
                uiStopwatch.Restart();
            };

            Process currentProcess = Process.GetCurrentProcess();
            ProcessPriorityClass originalPriority = currentProcess.PriorityClass;
            currentProcess.PriorityClass = ProcessPriorityClass.High;

            try
            {
                progress.Report("🔍 [第一階段] 正在對實體硬碟平行收集檔案資訊...");
                uiStopwatch.Restart();

                var enumOptions = new EnumerationOptions { 
                    IgnoreInaccessible = true, 
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = 0 
                };

                var excludeSet = new HashSet<string>(excludes, StringComparer.OrdinalIgnoreCase);

                var driveGroups = paths.GroupBy(p => Path.GetPathRoot(p)).ToList();
                var allValidFiles = new List<VideoFileInfo>();
                var allValidFilesLocker = new object();

                int statsDirs = 0;
                long lastStage1ReportTicks = 0;
                var stage1Stopwatch = Stopwatch.StartNew();

                await Parallel.ForEachAsync(driveGroups, new ParallelOptions { MaxDegreeOfParallelism = driveGroups.Count, CancellationToken = ct }, async (dg, token) =>
                {
                    var filesInDrive = new List<VideoFileInfo>();
                    var scannedRealPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    
                    var targetDirs = new Queue<DirectoryInfo>();
                    foreach (var startPath in dg)
                    {
                        if (Directory.Exists(startPath)) targetDirs.Enqueue(new DirectoryInfo(startPath));
                    }
                    const int MAX_DEPTH = 50;

                    await Task.Run(() =>
                    {
                        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
                        while (targetDirs.Count > 0 && !token.IsCancellationRequested)
                        {
                            var currentDir = targetDirs.Dequeue();
                            string d = currentDir.FullName;

                            if (excludeSet.Any(ex => d.StartsWith(ex, StringComparison.OrdinalIgnoreCase))) continue;

                            string realPath = d;
                            try { realPath = Path.GetFullPath(currentDir.FullName); } catch { }

                            if (!scannedRealPaths.Add(realPath)) continue; 

                            int depth = realPath.Count(c => c == Path.DirectorySeparatorChar);
                            if (depth > MAX_DEPTH) continue;

                            int currentDirs = Interlocked.Increment(ref statsDirs);
                            long currentTicks = stage1Stopwatch.ElapsedMilliseconds;
                            long lastTicks = Volatile.Read(ref lastStage1ReportTicks);
                            if (currentTicks - lastTicks > 200)
                            {
                                if (Interlocked.CompareExchange(ref lastStage1ReportTicks, currentTicks, lastTicks) == lastTicks)
                                {
                                    progress.Report(string.Format(Loc("ProgStage1Stats", "🔍 [第一階段] 正在平行掃描磁碟... 已搜集目錄: {0} | 篩選影片: {1} 檔"), currentDirs, statsFound));
                                }
                            }

                            if (includeSubDir)
                            {
                                try 
                                { 
                                    foreach (var sub in currentDir.EnumerateDirectories("*", enumOptions)) 
                                        targetDirs.Enqueue(sub); 
                                } 
                                catch { }
                            }

                            try
                            {
                                foreach (var fileInfo in currentDir.EnumerateFiles("*", enumOptions))
                                {
                                    if (token.IsCancellationRequested) break;

                                    if (FileHelper.IsSupportedVideoExtension(fileInfo.Name))
                                    {
                                        long len = fileInfo.Length; 
                                        if (len >= minSizeBytes)
                                        {
                                            filesInDrive.Add(new VideoFileInfo { 
                                                Path = fileInfo.FullName, 
                                                Length = len,
                                                LastWriteTimeTicks = fileInfo.LastWriteTimeUtc.Ticks
                                            });
                                            int currentFound = Interlocked.Increment(ref statsFound);

                                            currentTicks = stage1Stopwatch.ElapsedMilliseconds;
                                            lastTicks = Volatile.Read(ref lastStage1ReportTicks);
                                            if (currentTicks - lastTicks > 200)
                                            {
                                                if (Interlocked.CompareExchange(ref lastStage1ReportTicks, currentTicks, lastTicks) == lastTicks)
                                                {
                                                    progress.Report(string.Format(Loc("ProgStage1Stats", "🔍 [第一階段] 正在平行掃描磁碟... 已搜集目錄: {0} | 篩選影片: {1} 檔"), statsDirs, currentFound));
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }, token);

                    filesInDrive.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

                    lock (allValidFilesLocker)
                    {
                        allValidFiles.AddRange(filesInDrive);
                    }
                });

                ct.ThrowIfCancellationRequested();
                stage1Stopwatch.Stop();
                
                if (allValidFiles.Count > 0)
                {
                    long totalSizeBytes = allValidFiles.Sum(f => f.Length);
                    double totalSizeGb = totalSizeBytes / 1024.0 / 1024.0 / 1024.0;
                    progress.Report(string.Format(Loc("ProgStage1DoneInfo", "✅ [第一階段完成] 找到 {0} 個符合條件的影片，總計 {1:F2} GB。自動進入特徵解析..."), statsFound, totalSizeGb));
                    await Task.Delay(800, ct); 
                }
                else
                {
                    progress.Report(Loc("ProgStage1DoneZero", "✅ [第一階段完成] 掃描完畢，未找到符合條件的影片。"));
                    await Task.Delay(500, ct); 
                }

                var uncachedFiles = new List<VideoFileInfo>();
                foreach (var fileData in allValidFiles)
                {
                    string cacheKey = $"{fileData.Length}_{fileData.LastWriteTimeTicks}_{Path.GetFileName(fileData.Path)}_v9";
                    
                    if (_fingerprintCache.TryGetValue(cacheKey, out var cachedData) && cachedData != null)
                    {
                        if (cachedData.IsBadFile)
                        {
                            failedFilesBag.Add(new DupeFileItem { FilePath = fileData.Path, Size = fileData.Length, IsFailed = true, ErrorMessage = Loc("ErrDecodePoison", "影像解碼失敗 (記憶壞檔秒殺)") });
                            Interlocked.Increment(ref statsExtractFailed);
                            Interlocked.Increment(ref statsMetaProcessed);
                            continue;
                        }

                        if (cachedData.Fingerprints.Count > 0)
                        {
                            var headFp = cachedData.Fingerprints.FirstOrDefault(f => f.Position == FingerprintPosition.Head);
                            var tailFp = cachedData.Fingerprints.FirstOrDefault(f => f.Position == FingerprintPosition.Tail);
                            
                            var item = new DupeFileItem 
                            { 
                                FilePath = fileData.Path, 
                                Size = fileData.Length, 
                                CacheKey = cacheKey,
                                Duration = TimeSpan.FromSeconds(cachedData.DurationSec),
                                Width = cachedData.Width,
                                Height = cachedData.Height,
                                Resolution = (cachedData.Width > 0 && cachedData.Height > 0) ? $"{cachedData.Width}x{cachedData.Height}" : Loc("ResUnknown", "未知"),
                                HitCache = true,
                                HeadExtractSec = headFp?.TimeSec ?? 60,
                                TailExtractSec = tailFp?.TimeSec ?? 140
                            };
                            lock (item.Fingerprints) { item.Fingerprints.AddRange(cachedData.Fingerprints); }
                            lock (item.FullFingerprints) { item.FullFingerprints.AddRange(cachedData.FullFingerprints); }
                            
                            finalResultsBag.Add(item);
                            Interlocked.Increment(ref statsCacheHits);
                            Interlocked.Increment(ref statsMetaProcessed);
                        }
                        else
                        {
                            uncachedFiles.Add(fileData);
                        }
                    }
                    else
                    {
                        uncachedFiles.Add(fileData);
                    }
                }

                ReportProg(statsFound, statsMetaProcessed, statsFfmpegProcessed, statsCacheHits, statsExtractFailed, statsSkippedLocked, statsSkippedShort);

                if (uncachedFiles.Count == 0) progress.Report("⚡ [快取秒殺全滿貫] 所有檔案均由快取秒殺過濾！正在進行特徵比對...");
                else progress.Report($"🔬 記憶體秒殺完成！剩餘 {uncachedFiles.Count} 筆新檔案需要啟動 FFmpeg Worker Pool 限流讀寫管線...");

                var metadataChannel = Channel.CreateBounded<DupeFileItem>(new BoundedChannelOptions(cfg.MetadataBuffer) { FullMode = BoundedChannelFullMode.Wait });
                var driveIoSemaphores = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
                var keyLocks = new ConcurrentDictionary<string, Lazy<SemaphoreSlim>>();

                var metadataTask = Task.Run(async () =>
                {
                    try
                    {
                        await Parallel.ForEachAsync(uncachedFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, async (fileData, token) =>
                        {
                            string file = fileData.Path;
                            long sz = fileData.Length;
                            long ticks = fileData.LastWriteTimeTicks;

                            if (FileHelper.IsFileInUse(file))
                            {
                                Interlocked.Increment(ref statsSkippedLocked);
                                failedFilesBag.Add(new DupeFileItem { FilePath = file, Size = sz, IsFailed = true, ErrorMessage = Loc("ErrLocked", "被鎖定 (下載中/使用中)") });
                                int c1 = Interlocked.Increment(ref statsMetaProcessed);
                                ReportProg(statsFound, c1, statsFfmpegProcessed, statsCacheHits, statsExtractFailed, statsSkippedLocked, statsSkippedShort);
                                return;
                            }

                            string driveRoot = Path.GetPathRoot(file) ?? "DEFAULT";
                            var driveLock = driveIoSemaphores.GetOrAdd(driveRoot, _ => new SemaphoreSlim(1, 1));

                            var inspect = await FileHelper.InspectFileAsync(file, sz, ticks, _fingerprintCache, driveLock, token);
                            if (inspect == null)
                            {
                                Interlocked.Increment(ref statsSkippedLocked);
                                failedFilesBag.Add(new DupeFileItem { FilePath = file, Size = sz, IsFailed = true, ErrorMessage = Loc("ErrLocked", "被鎖定 (下載中/使用中)") });
                                int c1 = Interlocked.Increment(ref statsMetaProcessed);
                                ReportProg(statsFound, c1, statsFfmpegProcessed, statsCacheHits, statsExtractFailed, statsSkippedLocked, statsSkippedShort);
                                return;
                            }

                            string cacheKey = inspect.CacheKey;

                            if (inspect.IsBadOrDownloading)
                            {
                                failedFilesBag.Add(new DupeFileItem { FilePath = file, Size = sz, IsFailed = true, ErrorMessage = Loc("ErrTailHole", "不合法/損毀影片 (魔術數字攔截或空洞損毀)") });
                                Interlocked.Increment(ref statsExtractFailed);
                                int c1 = Interlocked.Increment(ref statsMetaProcessed);
                                ReportProg(statsFound, c1, statsFfmpegProcessed, statsCacheHits, statsExtractFailed, statsSkippedLocked, statsSkippedShort);
                                return;
                            }

                            if (inspect.IsCacheHit && inspect.CachedData != null)
                            {
                                if (inspect.CachedData.IsBadFile) 
                                {
                                    failedFilesBag.Add(new DupeFileItem { FilePath = file, Size = sz, IsFailed = true, ErrorMessage = Loc("ErrDecodePoison", "影像解碼失敗 (快取記憶壞檔)") });
                                    Interlocked.Increment(ref statsExtractFailed);
                                    int c1 = Interlocked.Increment(ref statsMetaProcessed);
                                    ReportProg(statsFound, c1, statsFfmpegProcessed, statsCacheHits, statsExtractFailed, statsSkippedLocked, statsSkippedShort);
                                    return;
                                }

                                if (inspect.CachedData.Fingerprints.Count > 0)
                                {
                                    var item = inspect.Metadata;
                                    if (item != null)
                                    {
                                        item.CacheKey = cacheKey;
                                        item.ContentCacheKey = inspect.ContentCacheKey;
                                        item.HitCache = true;
                                        
                                        lock (item.Fingerprints) { item.Fingerprints.AddRange(inspect.CachedData.Fingerprints); }
                                        lock (item.FullFingerprints) { item.FullFingerprints.AddRange(inspect.CachedData.FullFingerprints); }
                                        
                                        _fingerprintCache[cacheKey] = inspect.CachedData;
                                        
                                        Interlocked.Increment(ref statsCacheHits);
                                        await metadataChannel.Writer.WriteAsync(item, token);
                                    }
                                }
                            }
                            else
                            {
                                var item = inspect.Metadata;
                                if (item == null)
                                {
                                    string ext = Path.GetExtension(file).ToLower();
                                    if (AppConstants.LegacyVideoExts.Contains(ext)) {
                                        failedFilesBag.Add(new DupeFileItem { FilePath = file, Size = sz, IsFailed = true, IsFormatUpgradeNeeded = true, ErrorMessage = Loc("ErrLegacyFormat", "⚠️ 老舊格式 (需升級才能解析)") });
                                    } else {
                                        failedFilesBag.Add(new DupeFileItem { FilePath = file, Size = sz, IsFailed = true, ErrorMessage = Loc("ErrHeaderBad", "標頭損壞 (無法解析長度)") });
                                    }
                                    Interlocked.Increment(ref statsExtractFailed);
                                    int c1 = Interlocked.Increment(ref statsMetaProcessed);
                                    ReportProg(statsFound, c1, statsFfmpegProcessed, statsCacheHits, statsExtractFailed, statsSkippedLocked, statsSkippedShort);
                                    return;
                                }

                                item.CacheKey = cacheKey;
                                item.ContentCacheKey = inspect.ContentCacheKey;

                                if (item.Duration.TotalSeconds >= minLengthSec)
                                {
                                    await metadataChannel.Writer.WriteAsync(item, token);
                                }
                                else Interlocked.Increment(ref statsSkippedShort);
                            }

                            int current = Interlocked.Increment(ref statsMetaProcessed);
                            ReportProg(statsFound, current, statsFfmpegProcessed, statsCacheHits, statsExtractFailed, statsSkippedLocked, statsSkippedShort);
                        });
                    }
                    catch (OperationCanceledException) { }
                    finally { metadataChannel.Writer.Complete(); }
                }, ct);

                var ffmpegTask = Task.Run(async () =>
                {
                    try
                    {
                        int taskParallelism = cfg.IsSsd ? cfg.PipelineSlots : 50; 
                        
                        await Parallel.ForEachAsync(metadataChannel.Reader.ReadAllAsync(ct), new ParallelOptions { MaxDegreeOfParallelism = taskParallelism, CancellationToken = ct }, async (item, token) =>
                        {
                            if (item.HitCache && item.Fingerprints.Count > 0)
                            {
                                finalResultsBag.Add(item);
                                int ffmpegCurrent = Interlocked.Increment(ref statsFfmpegProcessed);
                                ReportProg(statsFound, statsMetaProcessed, ffmpegCurrent, statsCacheHits, statsExtractFailed, statsSkippedLocked, statsSkippedShort);
                                return;
                            }

                            string cacheKey = item.CacheKey; 

                            if (_fingerprintCache.TryGetValue(cacheKey, out var cachedData))
                            {
                                if (cachedData.IsBadFile) 
                                {
                                    item.IsFailed = true;
                                    item.ErrorMessage = Loc("ErrDecodePoison", "影像解碼失敗 (同源壞檔秒殺)");
                                    failedFilesBag.Add(item);
                                    Interlocked.Increment(ref statsExtractFailed);
                                    return;
                                }

                                lock (item.Fingerprints) { item.Fingerprints.AddRange(cachedData.Fingerprints); }
                                lock (item.FullFingerprints) { item.FullFingerprints.AddRange(cachedData.FullFingerprints); }
                                item.HitCache = true;
                            }
                            else
                            {
                                var myLock = keyLocks.GetOrAdd(cacheKey, _ => new Lazy<SemaphoreSlim>(() => new SemaphoreSlim(1, 1))).Value;

                                await myLock.WaitAsync(token);
                                try
                                {
                                    if (_fingerprintCache.TryGetValue(cacheKey, out var lateCachedData))
                                    {
                                        if (lateCachedData.IsBadFile) 
                                        {
                                            item.IsFailed = true;
                                            item.ErrorMessage = Loc("ErrDecodePoison", "影像解碼失敗 (快取記憶壞檔)");
                                            failedFilesBag.Add(item);
                                            Interlocked.Increment(ref statsExtractFailed);
                                            return;
                                        }

                                        lock (item.Fingerprints) { item.Fingerprints.AddRange(lateCachedData.Fingerprints); }
                                        lock (item.FullFingerprints) { item.FullFingerprints.AddRange(lateCachedData.FullFingerprints); }
                                        item.HitCache = true;
                                    }
                                    else
                                    {
                                        await FFmpegHelper.ExtractCombinedFingerprintsAsync(item, token, ffmpegTimeoutMs);

                                        if (item.Fingerprints.Count > 0)
                                        {
                                            var cacheDataToStore = new CachedVideoData 
                                            {
                                                DurationSec = item.Duration.TotalSeconds,
                                                Width = item.Width,
                                                Height = item.Height,
                                                Fingerprints = item.Fingerprints.ToList(),
                                                FullFingerprints = item.FullFingerprints.ToList(),
                                                IsBadFile = false
                                            };

                                            _fingerprintCache[cacheKey] = cacheDataToStore;
                                            if (!string.IsNullOrEmpty(item.ContentCacheKey))
                                            {
                                                _fingerprintCache[item.ContentCacheKey] = cacheDataToStore;
                                            }
                                        }
                                        else
                                        {
                                            item.IsFailed = true;
                                            item.ErrorMessage = Loc("ErrDecodeTimeout", "影像解碼失敗 (破圖/全黑/逾時)");
                                            failedFilesBag.Add(item);
                                            Interlocked.Increment(ref statsExtractFailed);
                                            
                                            var badCacheData = new CachedVideoData { IsBadFile = true };
                                            _fingerprintCache[cacheKey] = badCacheData;
                                            if (!string.IsNullOrEmpty(item.ContentCacheKey))
                                            {
                                                _fingerprintCache[item.ContentCacheKey] = badCacheData;
                                            }
                                        }
                                    }
                                }
                                finally { myLock.Release(); }
                            }

                            if (item.Fingerprints.Count > 0) finalResultsBag.Add(item);

                            int current = Interlocked.Increment(ref statsFfmpegProcessed);
                            ReportProg(statsFound, statsMetaProcessed, current, statsCacheHits, statsExtractFailed, statsSkippedLocked, statsSkippedShort);
                        });
                    }
                    catch (OperationCanceledException) { }
                }, ct);

                try { await Task.WhenAll(metadataTask, ffmpegTask); }
                catch (OperationCanceledException) { }

                ct.ThrowIfCancellationRequested();

                string finalFailMsg = statsExtractFailed > 0 ? string.Format(Loc("Microsoft JhengHei", "，⚠️ {0} 檔損壞"), statsExtractFailed) : "";
                string finalLockMsg = statsSkippedLocked > 0 ? string.Format(Loc("Microsoft JhengHei", "，🔒 跳過 {0} 個下載中/被鎖定檔案"), statsSkippedLocked) : "";
                string finalShortMsg = statsSkippedShort > 0 ? string.Format(Loc("Microsoft JhengHei", "，⏭️ 略過 {0} 個過短檔案"), statsSkippedShort) : "";
                progress.Report(string.Format(Loc("ProgDone", "⚡ [管線完成] 總計處理 {0} 檔，快取命中 {1} 次{2}{3}{4}。正在進行特徵比對..."), statsFfmpegProcessed, statsCacheHits, finalFailMsg, finalLockMsg, finalShortMsg));
                uiStopwatch.Restart();

                var suspectFiles = finalResultsBag.ToList();
                var groups = await Task.Run(() => ScanEngine.GroupFiles(suspectFiles));
                
                var finalFlatList = new List<DupeFileItem>();
                int displayGroupId = 1;

                foreach (var group in groups)
                {
                    finalFlatList.AddRange(ScanEngine.AssignGroupMetadata(group, displayGroupId++));
                }

                finalFlatList.AddRange(failedFilesBag);

                foreach (var kvp in driveIoSemaphores) kvp.Value.Dispose();

                return finalFlatList;
            }
            finally
            {
                try { currentProcess.PriorityClass = originalPriority; } catch { }
            }
        }

        private void FileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FileListView.SelectedItem is DupeFileItem item)
            {
                ShowPreviewAsync(item.FilePath);
            }
        }

        private async void ShowPreviewAsync(string path)
        {
            ReleasePreviewMemory();
            if (this.FindName("OpenBtn") is Button ob) ob.Visibility = Visibility.Visible;
            if (this.FindName("PreviewHintText") is TextBlock ht) ht.Visibility = Visibility.Collapsed;

            var targetItem = _cachedScanResults.FirstOrDefault(x => x.FilePath == path);
            UpdatePreviewButtonsState(targetItem);
            if (!File.Exists(path)) return;

            string ext = Path.GetExtension(path).ToLower();
            Guid previewId = Guid.NewGuid();
            _currentPreviewId = previewId;

            try
            {
                if (new[] { ".jpg", ".png", ".bmp", ".jpeg", ".gif", ".webp" }.Contains(ext))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    if (this.FindName("PreviewImage") is Image img) { img.Source = bitmap; img.Visibility = Visibility.Visible; }
                }
                else if (AppConstants.SupportedVideoExts.Contains(ext))
                {
                    double hSec = targetItem?.HeadExtractSec >= 0 ? targetItem.HeadExtractSec : 60;
                    double tSec = targetItem?.TailExtractSec >= 0 ? targetItem.TailExtractSec : hSec + 10;
                    
                    string overlayMsg = "";

                    if (targetItem != null && targetItem.IsReference)
                    {
                        overlayMsg = Loc("PrevRef", "📍 基準檔 (左至右：頭部 / 尾部)");
                    }
                    else if (targetItem != null)
                    {
                        overlayMsg = string.Format(Loc("PrevDupe", "左至右：頭部 / 尾部\n(與基準檔符合部位: {0})"), targetItem.MatchDetail);
                    }

                    if (this.FindName("PreviewText") is TextBox pt1) {
                        pt1.Text = Loc("PrevLoading", "⏳ 正在載入兩視角預覽圖...");
                        pt1.Visibility = Visibility.Visible;
                    }

                    byte[]? tripleBytes = await FFmpegHelper.GenerateDualPreviewThumbnailBytesAsync(path, hSec, tSec);
                    
                    if (_currentPreviewId != previewId) return;
                    RenderPreviewImage(tripleBytes, overlayMsg);
                }
            }
            catch (Exception ex)
            {
                if (_currentPreviewId == previewId)
                {
                    if (this.FindName("PreviewText") is TextBox pt2) {
                        pt2.Text = Loc("PrevErrMsg", "無法預覽: ") + ex.Message;
                        pt2.Visibility = Visibility.Visible;
                    }
                }
            }
        }

        private void RenderPreviewImage(byte[]? imageBytes, string overlayText)
        {
            if (imageBytes != null && imageBytes.Length > 0)
            {
                using var ms = new MemoryStream(imageBytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();

                if (this.FindName("PreviewImage") is Image img) { img.Source = bitmap; img.Visibility = Visibility.Visible; }
                
                if (this.FindName("PreviewText") is TextBox pt) {
                    pt.Text = overlayText;
                    pt.Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0));
                    pt.Foreground = Brushes.White;
                    pt.HorizontalAlignment = HorizontalAlignment.Center;
                    pt.VerticalAlignment = VerticalAlignment.Top;
                    pt.Padding = new Thickness(6, 4, 6, 4);
                    pt.Margin = new Thickness(0, 10, 0, 0);
                    pt.Visibility = Visibility.Visible;
                }
            }
            else
            {
                if (this.FindName("PreviewText") is TextBox pt) {
                    pt.Text = Loc("PrevErrDual", "❌ 無法產生預覽圖 (可能路徑包含特殊字元或嚴重損壞)");
                    pt.Visibility = Visibility.Visible;
                }
            }
        }

        private void ReleasePreviewMemory()
        {
            if (this.FindName("PreviewImage") is Image img) { img.Source = null; img.Visibility = Visibility.Collapsed; }

            if (this.FindName("PreviewText") is TextBox pt) {
                pt.Text = "";
                pt.Visibility = Visibility.Collapsed;
                pt.Background = Brushes.Transparent;
                pt.Foreground = Brushes.Black;
                pt.HorizontalAlignment = HorizontalAlignment.Stretch;
                pt.VerticalAlignment = VerticalAlignment.Center;
                pt.Margin = new Thickness(5);
            }

            if (this.FindName("PreviewHintText") is TextBlock ht) ht.Visibility = Visibility.Visible;
            if (this.FindName("OpenBtn") is Button ob) ob.Visibility = Visibility.Collapsed;
            if (this.FindName("RepairBtn") is Button rb) rb.Visibility = Visibility.Collapsed;
            if (this.FindName("UpgradeBtn") is Button ub) ub.Visibility = Visibility.Collapsed;
        }

        private void OpenDirectory_Click(object sender, RoutedEventArgs e)
        {
            if (FileListView.SelectedItem is DupeFileItem item && !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                Process.Start("explorer.exe", $"/select,\"{item.FilePath}\"");
        }

        private void FileListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileListView.SelectedItem is DupeFileItem item && !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                Process.Start(new ProcessStartInfo { FileName = item.FilePath, UseShellExecute = true });
        }

        private void FileListView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete) DeleteSelectedFiles();
        }

        private void DeleteSelected_Click(object sender, RoutedEventArgs e) => DeleteSelectedFiles();

        private async void DeleteSelectedFiles()
        {
            var selectedItems = FileListView.SelectedItems.Cast<DupeFileItem>().ToList();
            if (!selectedItems.Any()) return;

            string msg = string.Format(Loc("MsgConfirmDel", "確定要將選取的 {0} 個檔案移至資源回收桶嗎？"), selectedItems.Count);
            if (MessageBox.Show(msg, Loc("TitleConfirmDel", "確認刪除"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            ReleasePreviewMemory();
            if (this.FindName("DeleteBtn") is Button delBtn) delBtn.IsEnabled = false;
            StatusText.Text = Loc("StatusDeleting", "🗑️ 正在批量刪除檔案，請稍候...");

            int successCount = 0;
            var deletedItems = new List<DupeFileItem>();
            var uiStopwatch = Stopwatch.StartNew(); 

            await Task.Run(() =>
            {
                int processed = 0;
                foreach (var item in selectedItems)
                {
                    if (FileHelper.SendToRecycleBin(item.FilePath))
                    {
                        deletedItems.Add(item);
                        successCount++;
                    }
                    processed++;
                    
                    if (uiStopwatch.ElapsedMilliseconds >= 3000 || processed == selectedItems.Count)
                    {
                        Application.Current.Dispatcher.InvokeAsync(() => {
                            StatusText.Text = string.Format(Loc("StatusDeletingProgress", "🗑️ 正在刪除檔案... ({0}/{1})"), processed, selectedItems.Count);
                        });
                        uiStopwatch.Restart();
                    }
                }
            });

            var deletedSet = new HashSet<DupeFileItem>(deletedItems);
            _cachedScanResults.RemoveAll(x => deletedSet.Contains(x));
            ApplyFilter(); 

            StatusText.Text = string.Format(Loc("StatusDelDone", "🗑️ 已將 {0} 個檔案移至資源回收桶"), successCount);
            UpdateGlobalUIState();
        }

        private void OpenBtn_Click(object sender, RoutedEventArgs e)
        {
            if (FileListView.SelectedItem is DupeFileItem item && File.Exists(item.FilePath)) 
                Process.Start(new ProcessStartInfo { FileName = item.FilePath, UseShellExecute = true });
        }

        private void ToggleLangBtn_Click(object sender, RoutedEventArgs e) => I18nManager.Instance.ToggleLanguage();
        private void ClearPath_Click(object sender, RoutedEventArgs e) { if (this.FindName("PathTextBox") is TextBox pt) pt.Text = ""; }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var d = new OpenFolderDialog();
            if (d.ShowDialog() == true && this.FindName("PathTextBox") is TextBox pt)
                pt.Text = string.IsNullOrWhiteSpace(pt.Text) ? d.FolderName : $"{pt.Text}; {d.FolderName}";
        }

        private void BrowseExcludeFolder_Click(object sender, RoutedEventArgs e)
        {
            var d = new OpenFolderDialog();
            if (d.ShowDialog() == true && this.FindName("ExcludePathTextBox") is TextBox et)
                et.Text = string.IsNullOrWhiteSpace(et.Text) ? d.FolderName : $"{et.Text}; {d.FolderName}";
        }
    }
}