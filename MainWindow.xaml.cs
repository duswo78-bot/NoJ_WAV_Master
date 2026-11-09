using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NoJ_WAV_Master;

public partial class MainWindow : Window
{
    private const string PlayGeometry = "M1,0 L10,5.5 L1,11 Z";
    private const string PauseGeometry = "M0,0 L3,0 L3,10 L0,10 Z M6,0 L9,0 L9,10 L6,10 Z";
    private const string FFmpegZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".flac", ".aiff", ".aif", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".mp4", ".mkv", ".mov"
    };

    public ObservableCollection<AudioTaskItem> Tasks { get; } = new();
    private readonly MediaPlayer _rawPlayer = new();
    private MediaPlayer _masterPlayer = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _previewDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(95) };
    private readonly SemaphoreSlim _ffmpegLock = new(1, 1);
    private AudioTaskItem? _currentItem;
    private bool _isPlaying;
    private bool _isProcessing;
    private bool _settingsDirty;
    private bool _suppressAutoManual;
    private bool _autoSwitchingToManual;
    private bool _pendingSwitchToAfter;
    private int _previewVersion;
    private int _previewFileSlot;
    private string? _ffmpegPath;

    private bool HasAfterAudio(AudioTaskItem? item) =>
        item != null && ((!string.IsNullOrEmpty(item.PreviewPath) && File.Exists(item.PreviewPath)) ||
                         (!string.IsNullOrEmpty(item.OutputPath) && File.Exists(item.OutputPath)));

    private MediaPlayer ActivePlayer => (BtnAfter.IsChecked == true && HasAfterAudio(_currentItem))
        ? _masterPlayer
        : _rawPlayer;
    private MediaPlayer InactivePlayer => ReferenceEquals(ActivePlayer, _masterPlayer) ? _rawPlayer : _masterPlayer;

    public MainWindow()
    {
        InitializeComponent();
        FileList.ItemsSource = Tasks;
        _timer.Tick += Timer_Tick;
        _previewDebounceTimer.Tick += PreviewDebounceTimer_Tick;
        _rawPlayer.MediaOpened += Player_MediaOpened;
        _masterPlayer.MediaOpened += Player_MediaOpened;
        _rawPlayer.MediaEnded += Player_MediaEnded;
        _masterPlayer.MediaEnded += Player_MediaEnded;
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        ReleasePlayers();
        CleanupPreviewTempFiles();
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            e.Handled = true;
            PlayPause_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.Tab || e.Key == Key.B)
        {
            if (_currentItem != null)
            {
                e.Handled = true;
                if (BtnBefore.IsChecked == true && HasAfterAudio(_currentItem))
                    BtnAfter.IsChecked = true;
                else
                    BtnBefore.IsChecked = true;
            }
        }
        else if (e.Key == Key.Left || e.Key == Key.Right)
        {
            var player = ActivePlayer;
            if (_currentItem != null && player.NaturalDuration.HasTimeSpan)
            {
                e.Handled = true;
                double delta = e.Key == Key.Right ? 3.0 : -3.0;
                double target = Math.Clamp(player.Position.TotalSeconds + delta, 0, player.NaturalDuration.TimeSpan.TotalSeconds);
                player.Position = TimeSpan.FromSeconds(target);
                Playhead.Visibility = Visibility.Visible;
                UpdateTimeAndPlayhead();
            }
        }
        else if (e.Key == Key.Delete && !_isProcessing && _currentItem != null)
        {
            e.Handled = true;
            RemoveTrackItem(_currentItem);
        }
    }

    private void OpenTrackFolder_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is AudioTaskItem item)
        {
            string targetFile = (!string.IsNullOrEmpty(item.OutputPath) && File.Exists(item.OutputPath))
                ? item.OutputPath
                : item.FilePath;
            if (File.Exists(targetFile))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{targetFile}\"") { UseShellExecute = true });
                }
                catch { }
            }
        }
    }

    private void RemoveTrack_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_isProcessing) return;
        if (sender is FrameworkElement fe && fe.Tag is AudioTaskItem item)
            RemoveTrackItem(item);
    }

    private void RemoveTrackItem(AudioTaskItem item)
    {
        int idx = Tasks.IndexOf(item);
        if (idx < 0) return;

        bool wasCurrent = ReferenceEquals(_currentItem, item);
        if (wasCurrent)
        {
            ReleasePlayers();
            _currentItem = null;
        }

        Tasks.RemoveAt(idx);

        if (Tasks.Count == 0)
        {
            Clear_Click(this, new RoutedEventArgs());
            return;
        }

        if (wasCurrent)
        {
            int nextIdx = Math.Clamp(idx, 0, Tasks.Count - 1);
            FileList.SelectedItem = Tasks[nextIdx];
        }
        UpdateStartButtonState();
    }

    private void ToggleRack_Click(object sender, RoutedEventArgs e)
    {
        bool open = ToggleRackBtn.IsChecked == true;
        FxRackPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        Width = open ? 880 : 560;
    }

    private void ModeManual_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (ToggleRackBtn != null && ToggleRackBtn.IsChecked == false)
            ToggleRackBtn.IsChecked = true;

        // If user clicked "Manual FX" card directly after "Convert Only" (where all FX were unchecked), restore base rack
        if (!_autoSwitchingToManual &&
            ChkAtmos.IsChecked != true && ChkEqCut.IsChecked != true && ChkLimiter.IsChecked != true &&
            ChkLoudness.IsChecked != true && ChkWarmth.IsChecked != true && ChkReverb.IsChecked != true &&
            ChkStereo.IsChecked != true && ChkPitch.IsChecked != true)
        {
            _suppressAutoManual = true;
            try
            {
                ChkEqCut.IsChecked = true;
                SldLowCut.Value = 20;
                SldHighCut.Value = 18000;
                ChkLimiter.IsChecked = true;
                SldLimit.Value = -1.0;
            }
            finally
            {
                _suppressAutoManual = false;
            }
        }

        MarkSettingsChanged(switchToAfter: true);
    }

    private void SyncRackToEngineMode(object sender)
    {
        _suppressAutoManual = true;
        try
        {
            if (ReferenceEquals(sender, ModeClean))
            {
                ChkAtmos.IsChecked = false;
                ChkEqCut.IsChecked = true;
                SldLowCut.Value = 20;
                SldHighCut.Value = 18000;
                ChkLimiter.IsChecked = true;
                SldLimit.Value = -1.0;
                ChkLoudness.IsChecked = false;
                ChkWarmth.IsChecked = false;
                ChkReverb.IsChecked = false;
                ChkStereo.IsChecked = false;
                ChkPitch.IsChecked = false;
            }
            else if (ReferenceEquals(sender, ModeLoud))
            {
                ChkAtmos.IsChecked = false;
                ChkEqCut.IsChecked = true;
                SldLowCut.Value = 20;
                SldHighCut.Value = 18000;
                ChkLimiter.IsChecked = true;
                SldLimit.Value = -1.0;
                ChkLoudness.IsChecked = true;
                SldLufs.Value = -14;
                ChkWarmth.IsChecked = false;
                ChkReverb.IsChecked = false;
                ChkStereo.IsChecked = false;
                ChkPitch.IsChecked = false;
            }
            else if (ReferenceEquals(sender, ModeConvert))
            {
                ChkAtmos.IsChecked = false;
                ChkEqCut.IsChecked = false;
                ChkLimiter.IsChecked = false;
                ChkLoudness.IsChecked = false;
                ChkWarmth.IsChecked = false;
                ChkReverb.IsChecked = false;
                ChkStereo.IsChecked = false;
                ChkPitch.IsChecked = false;
            }
        }
        finally
        {
            _suppressAutoManual = false;
        }
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool isModeChange = ReferenceEquals(sender, ModeClean) || ReferenceEquals(sender, ModeLoud) || ReferenceEquals(sender, ModeConvert);
        if (isModeChange)
        {
            SyncRackToEngineMode(sender);
        }
        MarkSettingsChanged(switchToAfter: isModeChange);
    }

    private void FxControl_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressAutoManual) return;
        if (ModeManual.IsChecked != true)
        {
            _autoSwitchingToManual = true;
            ModeManual.IsChecked = true;
            _autoSwitchingToManual = false;
        }
        MarkSettingsChanged(switchToAfter: true);
    }

    private void FxSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _suppressAutoManual) return;
        if (ModeManual.IsChecked != true)
        {
            _autoSwitchingToManual = true;
            ModeManual.IsChecked = true;
            _autoSwitchingToManual = false;
        }
        MarkSettingsChanged(switchToAfter: true);
    }

    private void TrimFade_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressAutoManual) return;
        MarkSettingsChanged(switchToAfter: true);
    }

    private void TrimFadeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _suppressAutoManual) return;
        if (ReferenceEquals(sender, SldTrimDb) && ChkTrimSilence.IsChecked != true)
            ChkTrimSilence.IsChecked = true;
        if (ReferenceEquals(sender, SldFadeOut) && ChkFadeOut.IsChecked != true)
            ChkFadeOut.IsChecked = true;
        MarkSettingsChanged(switchToAfter: true);
    }

    private void MarkSettingsChanged(bool switchToAfter = false)
    {
        if (!IsLoaded) return;
        if (Tasks.Any(t => !string.IsNullOrEmpty(t.OutputPath)))
        {
            _settingsDirty = true;
        }
        UpdateStartButtonState();

        if (_currentItem != null)
        {
            if (switchToAfter) _pendingSwitchToAfter = true;
            _previewDebounceTimer.Stop();
            _previewDebounceTimer.Start();
        }
    }

    private async void PreviewDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _previewDebounceTimer.Stop();
        bool switchAfter = _pendingSwitchToAfter;
        _pendingSwitchToAfter = false;
        if (_currentItem != null && !_isProcessing)
        {
            await RenderLivePreviewForItemAsync(_currentItem, switchToAfter: switchAfter);
        }
    }

    private void UpdateStartButtonState()
    {
        if (StartBtn == null) return;

        if (_isProcessing)
        {
            StartBtn.IsEnabled = false;
            StartBtn.Content = "PROCESSING TRACKS...";
            return;
        }

        if (Tasks.Count == 0)
        {
            StartBtn.IsEnabled = false;
            StartBtn.Content = "DROP OR ADD TRACKS TO START";
            return;
        }

        StartBtn.IsEnabled = true;
        bool isConvertOnly = ModeConvert?.IsChecked == true;
        bool anyUnprocessed = Tasks.Any(t => string.IsNullOrEmpty(t.OutputPath));
        string countLabel = $"{Tasks.Count} {(Tasks.Count == 1 ? "TRACK" : "TRACKS")}";

        if (anyUnprocessed)
        {
            StartBtn.Content = isConvertOnly
                ? $"START CONVERTING ({countLabel})"
                : $"START MASTERING & CONVERT ({countLabel})";
        }
        else if (_settingsDirty)
        {
            StartBtn.Content = isConvertOnly
                ? $"APPLY & RE-CONVERT ({countLabel})"
                : $"APPLY NEW FX & RE-MASTER ({countLabel})";
        }
        else
        {
            StartBtn.Content = isConvertOnly
                ? $"RE-CONVERT ALL ({countLabel})"
                : $"RE-MASTER ALL ({countLabel})";
        }
    }

    private void ResetFx_Click(object sender, RoutedEventArgs e)
    {
        _suppressAutoManual = true;
        try
        {
            ChkAtmos.IsChecked = false;
            SldAtmosDepth.Value = 50;
            SldAtmosSurround.Value = 155;
            SldAtmosLfe.Value = 3.5;
            ChkEqCut.IsChecked = true;
            SldLowCut.Value = 20;
            SldHighCut.Value = 18000;
            ChkLimiter.IsChecked = true;
            SldLimit.Value = -1.0;
            ChkLoudness.IsChecked = false;
            SldLufs.Value = -14;
            ChkWarmth.IsChecked = false;
            SldWarmth.Value = 25;
            ChkReverb.IsChecked = false;
            SldDelay.Value = 65;
            SldDecay.Value = 22;
            ChkStereo.IsChecked = false;
            SldStereo.Value = 135;
            ChkPitch.IsChecked = false;
            SldPitch.Value = 0;
            ChkTrimSilence.IsChecked = false;
            ChkFadeOut.IsChecked = false;
            SldTrimDb.Value = -50;
            SldFadeOut.Value = 2.0;
        }
        finally
        {
            _suppressAutoManual = false;
        }
        MarkSettingsChanged(switchToAfter: true);
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            DropZone.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6E7BF2"));
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252834"));
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252834"));
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[] dropped = (string[])e.Data.GetData(DataFormats.FileDrop);
            await EnqueueFilesAsync(dropped);
        }
    }

    private async void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        if (Tasks.Count > 0) return;
        await BrowseAndEnqueueFilesAsync();
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        await BrowseAndEnqueueFilesAsync();
    }

    private async Task BrowseAndEnqueueFilesAsync()
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Audio/Video Files|*.wav;*.mp3;*.flac;*.aiff;*.aac;*.m4a;*.ogg;*.mp4|All Files|*.*"
        };
        if (dlg.ShowDialog() == true)
            await EnqueueFilesAsync(dlg.FileNames);
    }

    private async void StartProcess_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing || Tasks.Count == 0) return;
        await ExecuteBatchAsync();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;
        ReleasePlayers();
        _currentItem = null;
        _settingsDirty = false;
        WaveformCanvas.Children.Clear();
        NowPlayingText.Text = "Select a track to inspect & preview";
        TimeText.Text = "00:00 / 00:00";
        DiffStatsText.Text = "Overlay Diff Ready";
        ResetQcDisplay();
        Tasks.Clear();
        FileList.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        ClearBtn.Visibility = Visibility.Collapsed;
        AddFilesBtn.Visibility = Visibility.Collapsed;
        StatusText.Text = "Ready · EBU R128 Audio QC Inspector active";
        UpdateStartButtonState();
    }

    private void ResetQcDisplay()
    {
        if (TxtLufs == null) return;
        TxtLufs.Text = "-- LUFS";
        TxtLufs.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D5D8E2"));
        TxtPeak.Text = "-- dBTP";
        TxtPeak.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D5D8E2"));
        TxtLra.Text = "-- LU";
        TxtGrade.Text = "STANDBY";
        TxtGrade.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8E93A6"));
        GradeBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#222634"));
    }

    private static List<string> ExpandInputFiles(string[] paths)
    {
        var result = new List<string>();
        foreach (string p in paths)
        {
            if (File.Exists(p))
            {
                result.Add(p);
            }
            else if (Directory.Exists(p))
            {
                foreach (string child in Directory.EnumerateFiles(p, "*.*", SearchOption.TopDirectoryOnly))
                {
                    if (SupportedExtensions.Contains(System.IO.Path.GetExtension(child)))
                        result.Add(child);
                }
            }
        }
        return result;
    }

    private static string? FindExistingFFmpeg()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string exeDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        string[] candidates =
        [
            System.IO.Path.Combine(exeDir, "ffmpeg.exe"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            System.IO.Path.Combine(localAppData, "NoJ_WAV_Master", "ffmpeg", "ffmpeg.exe"),
            System.IO.Path.Combine(localAppData, "JonPlayer", "ffmpeg", "ffmpeg.exe"),
            System.IO.Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe"),
            @"C:\ffmpeg\bin\ffmpeg.exe"
        ];

        foreach (string c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        string wingetPkgs = System.IO.Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetPkgs))
        {
            try
            {
                string? wgMatch = Directory.EnumerateFiles(wingetPkgs, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (wgMatch != null) return wgMatch;
            }
            catch { }
        }

        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in pathEnv.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string full = System.IO.Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(full)) return full;
            }
            catch { }
        }

        return null;
    }

    private async Task<string> EnsureFFmpegAsync()
    {
        if (!string.IsNullOrEmpty(_ffmpegPath) && File.Exists(_ffmpegPath))
            return _ffmpegPath;

        await _ffmpegLock.WaitAsync();
        try
        {
            if (!string.IsNullOrEmpty(_ffmpegPath) && File.Exists(_ffmpegPath))
                return _ffmpegPath;

            string? existing = FindExistingFFmpeg();
            if (existing != null)
            {
                _ffmpegPath = existing;
                return existing;
            }

            string targetDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NoJ_WAV_Master",
                "ffmpeg");
            Directory.CreateDirectory(targetDir);

            string finalExe = System.IO.Path.Combine(targetDir, "ffmpeg.exe");
            string tempZip = System.IO.Path.Combine(targetDir, "ffmpeg_download.zip");
            string tempExe = System.IO.Path.Combine(targetDir, "ffmpeg.exe.tmp");

            StatusText.Text = "Installing FFmpeg engine (first-time setup)... 0%";

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var resp = await http.GetAsync(FFmpegZipUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long? totalBytes = resp.Content.Headers.ContentLength;

                await using (var netStream = await resp.Content.ReadAsStreamAsync())
                await using (var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    byte[] buffer = new byte[81920];
                    long downloaded = 0;
                    int lastPct = -1;
                    int read;
                    while ((read = await netStream.ReadAsync(buffer)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read));
                        downloaded += read;
                        if (totalBytes is > 0)
                        {
                            int pct = (int)(downloaded * 100 / totalBytes.Value);
                            if (pct != lastPct)
                            {
                                lastPct = pct;
                                StatusText.Text = $"Installing FFmpeg engine (first-time setup)... {pct}%";
                            }
                        }
                    }
                }
            }

            StatusText.Text = "Extracting FFmpeg audio engine...";
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(tempZip);
                var entry = archive.Entries.FirstOrDefault(e =>
                    string.Equals(e.Name, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new FileNotFoundException("ffmpeg.exe not found in archive.");

                entry.ExtractToFile(tempExe, true);
                File.Move(tempExe, finalExe, true);
            });

            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }

            _ffmpegPath = finalExe;
            return finalExe;
        }
        finally
        {
            _ffmpegLock.Release();
        }
    }

    private async Task EnqueueFilesAsync(string[] rawPaths)
    {
        List<string> files = ExpandInputFiles(rawPaths);
        if (files.Count == 0)
        {
            if (Tasks.Count == 0)
            {
                EmptyState.Visibility = Visibility.Visible;
                FileList.Visibility = Visibility.Collapsed;
            }
            UpdateStartButtonState();
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        FileList.Visibility = Visibility.Visible;
        ClearBtn.Visibility = Visibility.Visible;
        AddFilesBtn.Visibility = Visibility.Visible;

        AudioTaskItem? firstAdded = null;
        int addedCount = 0;

        foreach (var f in files)
        {
            var existing = Tasks.FirstOrDefault(t => string.Equals(t.FilePath, f, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                firstAdded ??= existing;
                continue;
            }

            var item = new AudioTaskItem
            {
                FilePath = f,
                FileName = System.IO.Path.GetFileName(f),
                Status = "Ready",
                StatusColor = "#9EA6F6"
            };
            Tasks.Add(item);
            firstAdded ??= item;
            addedCount++;
        }

        UpdateStartButtonState();
        StatusText.Text = $"{Tasks.Count} track(s) loaded · Adjust settings & click Start below";

        if (FileList.SelectedItem == null && firstAdded != null)
        {
            FileList.SelectedItem = firstAdded;
        }
        else if (addedCount == 0 && firstAdded != null)
        {
            FileList.SelectedItem = firstAdded;
        }

        await Task.CompletedTask;
    }

    private async Task ExecuteBatchAsync()
    {
        if (Tasks.Count == 0 || _isProcessing) return;

        _isProcessing = true;
        UpdateStartButtonState();

        string? prevSelectedFile = (FileList.SelectedItem as AudioTaskItem)?.FilePath;

        string ffmpeg;
        try
        {
            ffmpeg = await EnsureFFmpegAsync();
        }
        catch
        {
            _isProcessing = false;
            UpdateStartButtonState();
            StatusText.Text = "FFmpeg setup failed. Check internet or place ffmpeg.exe next to app.";
            return;
        }

        string filterArg = BuildFilterArgument();
        var (ext, codecArg, suffix, fmtLabel) = GetFormatArguments();

        StatusText.Text = $"Processing {Tasks.Count} file(s)...";
        int successCount = 0;

        ReleasePlayers();
        _currentItem = null;

        foreach (var item in Tasks)
        {
            item.Status = "Working...";
            item.StatusColor = "#7C85F2";
            item.MasterPeaks = null;
            item.MasterPeakLinear = 0f;
            item.MasterRmsLinear = 0f;
            item.MasterQc = null;

            var (ok, outPath) = await Task.Run(() => RunFFmpeg(ffmpeg, item.FilePath, filterArg, codecArg, ext, suffix));
            if (ok)
            {
                item.OutputPath = outPath;
                item.Status = $"Done ({fmtLabel})";
                item.StatusColor = "#4ADE80";
                successCount++;
            }
            else
            {
                item.OutputPath = "";
                item.Status = "Failed";
                item.StatusColor = "#F87171";
            }
        }

        _isProcessing = false;
        _settingsDirty = false;
        UpdateStartButtonState();

        StatusText.Text = $"Completed {successCount}/{Tasks.Count} file(s) · Click a track to compare Before/After";

        AudioTaskItem? targetSelect = null;
        if (!string.IsNullOrEmpty(prevSelectedFile))
        {
            targetSelect = Tasks.FirstOrDefault(i =>
                string.Equals(i.FilePath, prevSelectedFile, StringComparison.OrdinalIgnoreCase));
        }
        targetSelect ??= Tasks.FirstOrDefault(i => !string.IsNullOrEmpty(i.OutputPath)) ?? Tasks.FirstOrDefault();

        if (targetSelect != null)
        {
            FileList.SelectedItem = null;
            FileList.SelectedItem = targetSelect;
        }
    }

    private string BuildFilterArgument()
    {
        var filters = new List<string>();
        var inv = CultureInfo.InvariantCulture;

        if (ModeClean.IsChecked == true)
        {
            filters.Add("highpass=f=20:poles=2");
            filters.Add("lowpass=f=18000:poles=2");
            filters.Add("alimiter=limit=0.891:level=disabled:attack=5:release=50");
        }
        else if (ModeLoud.IsChecked == true)
        {
            filters.Add("highpass=f=20:poles=2");
            filters.Add("lowpass=f=18000:poles=2");
            filters.Add("loudnorm=I=-14:TP=-1.0:LRA=11");
        }
        else if (ModeManual.IsChecked == true)
        {
            if (ChkEqCut.IsChecked == true)
            {
                filters.Add($"highpass=f={(int)SldLowCut.Value}:poles=2");
                filters.Add($"lowpass=f={(int)SldHighCut.Value}:poles=2");
            }

            if (ChkPitch.IsChecked == true && (int)SldPitch.Value != 0)
            {
                double factor = Math.Pow(2.0, SldPitch.Value / 12.0);
                filters.Add($"aresample=44100,asetrate=44100*{factor.ToString("0.0000", inv)},aresample=44100");
            }

            if (ChkWarmth.IsChecked == true && SldWarmth.Value > 0)
            {
                double gain = 1.0 + (SldWarmth.Value / 100.0) * 2.5;
                filters.Add($"acompressor=threshold=-16dB:ratio=2.5:makeup={gain.ToString("0.00", inv)}");
            }

            if (ChkAtmos.IsChecked == true)
            {
                if (SldAtmosLfe.Value > 0.05)
                {
                    filters.Add($"bass=g={SldAtmosLfe.Value.ToString("0.0", inv)}:f=60:w=0.5");
                }

                if (SldAtmosDepth.Value > 0)
                {
                    double depthNorm = SldAtmosDepth.Value / 100.0;
                    double lDelay = 1.6 + depthNorm * 5.2;
                    double rDelay = 2.1 + depthNorm * 6.4;
                    double sideGain = 1.0 + depthNorm * 0.65;
                    double airGain = depthNorm * 2.8;
                    filters.Add($"haas=level_in=1:level_out=1:side_gain={sideGain.ToString("0.00", inv)}:left_delay={lDelay.ToString("0.00", inv)}:right_delay={rDelay.ToString("0.00", inv)}");
                    if (airGain >= 0.2)
                        filters.Add($"treble=g={airGain.ToString("0.0", inv)}:f=11000:w=0.6");
                }

                if (SldAtmosSurround.Value > 100)
                {
                    double surrNorm = (SldAtmosSurround.Value - 100.0) / 120.0;
                    double slev = 1.0 + surrNorm * 0.95;
                    double sbase = Math.Clamp(surrNorm * 0.45, 0.0, 0.65);
                    filters.Add($"stereotools=mlev=1.0:slev={slev.ToString("0.00", inv)}:base={sbase.ToString("0.00", inv)}:softclip=true");
                }
            }

            if (ChkStereo.IsChecked == true && SldStereo.Value > 100)
            {
                double mlev = 1.0 + ((SldStereo.Value - 100.0) / 100.0) * 0.8;
                filters.Add($"extrastereo=m={mlev.ToString("0.00", inv)}");
            }

            if (ChkReverb.IsChecked == true)
            {
                int delay = (int)SldDelay.Value;
                double decay = Math.Clamp(SldDecay.Value / 100.0, 0.05, 0.85);
                filters.Add($"aecho=0.8:0.88:{delay}:{decay.ToString("0.00", inv)}");
            }

            if (ChkLoudness.IsChecked == true)
            {
                int lufs = (int)SldLufs.Value;
                filters.Add($"loudnorm=I={lufs}:TP=-1.0:LRA=11");
            }

            if (ChkLimiter.IsChecked == true)
            {
                double db = SldLimit.Value;
                double linear = Math.Pow(10.0, db / 20.0);
                filters.Add($"alimiter=limit={linear.ToString("0.000", inv)}:level=disabled:attack=5:release=50");
            }
        }

        bool doTrim = ChkTrimSilence.IsChecked == true;
        bool doFade = ChkFadeOut.IsChecked == true;
        if (doTrim || doFade)
        {
            int trimDb = (int)SldTrimDb.Value;
            double fadeSec = Math.Clamp(SldFadeOut.Value, 0.3, 10.0);

            if (doTrim)
            {
                filters.Add($"silenceremove=start_periods=1:start_duration=0.01:start_threshold={trimDb}dB:detection=peak");
                filters.Add("afade=t=in:st=0:d=0.015:curve=qsin");
            }

            filters.Add("areverse");

            if (doTrim)
            {
                filters.Add($"silenceremove=start_periods=1:start_duration=0.02:start_threshold={trimDb}dB:detection=peak");
            }

            if (doFade)
            {
                filters.Add($"afade=t=in:st=0:d={fadeSec.ToString("0.00", inv)}:curve=qsin");
            }

            filters.Add("areverse");
        }

        return filters.Count > 0 ? $"-af \"{string.Join(",", filters)}\"" : "";
    }

    private (string ext, string codec, string suffix, string label) GetFormatArguments()
    {
        string modeSuffix = ModeConvert.IsChecked == true ? "_Converted" : "_Mastered";

        if (FmtWav24.IsChecked == true)  return (".wav", "-ar 44100 -ac 2 -c:a pcm_s24le", modeSuffix, "WAV 24");
        if (FmtWav16.IsChecked == true)  return (".wav", "-ar 44100 -ac 2 -c:a pcm_s16le", modeSuffix, "WAV 16");
        if (FmtFlac16.IsChecked == true) return (".flac", "-ar 44100 -ac 2 -sample_fmt s16 -c:a flac -compression_level 8", modeSuffix, "FLAC 16");
        if (FmtFlac24.IsChecked == true) return (".flac", "-ar 44100 -ac 2 -sample_fmt s32 -bits_per_raw_sample 24 -c:a flac -compression_level 8", modeSuffix, "FLAC 24");
        if (FmtMp3.IsChecked == true)    return (".mp3", "-ar 44100 -ac 2 -c:a libmp3lame -b:a 320k", modeSuffix, "MP3");
        if (FmtAiff.IsChecked == true)   return (".aiff", "-ar 44100 -ac 2 -c:a pcm_s24be", modeSuffix, "AIFF");
        if (FmtAac.IsChecked == true)    return (".m4a", "-ar 44100 -ac 2 -c:a aac -b:a 256k", modeSuffix, "AAC");

        return (".wav", "-ar 44100 -ac 2 -c:a pcm_s24le", modeSuffix, "WAV 24");
    }

    private static string BuildOutputPath(string inputPath, string suffix, string ext)
    {
        string dir = System.IO.Path.GetDirectoryName(inputPath) ?? "";
        string name = System.IO.Path.GetFileNameWithoutExtension(inputPath);
        string outPath = System.IO.Path.Combine(dir, $"{name}{suffix}{ext}");
        if (string.Equals(inputPath, outPath, StringComparison.OrdinalIgnoreCase))
            outPath = System.IO.Path.Combine(dir, $"{name}{suffix}_New{ext}");
        return outPath;
    }

    private static (bool ok, string outPath) RunFFmpeg(string ffmpegPath, string inputPath, string filterArg, string codecArg, string ext, string suffix)
    {
        try
        {
            string outPath = BuildOutputPath(inputPath, suffix, ext);
            string args = $"-y -v error -i \"{inputPath}\" -vn -sn -dn -map_metadata -1 -fflags +bitexact -flags:a +bitexact {filterArg} {codecArg} \"{outPath}\"";

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (false, "");
            _ = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode == 0 && File.Exists(outPath) && new FileInfo(outPath).Length > 0, outPath);
        }
        catch
        {
            return (false, "");
        }
    }

    private LiveFxSettings CaptureCurrentFxSettings()
    {
        EngineMode mode = ModeConvert.IsChecked == true ? EngineMode.ConvertOnly
            : ModeClean.IsChecked == true ? EngineMode.Clean
            : ModeLoud.IsChecked == true ? EngineMode.Loud
            : EngineMode.Manual;

        return new LiveFxSettings(
            Mode: mode,
            AtmosEnabled: ChkAtmos.IsChecked == true,
            AtmosDepth: SldAtmosDepth.Value,
            AtmosSurround: SldAtmosSurround.Value,
            AtmosLfeDb: SldAtmosLfe.Value,
            EqEnabled: ChkEqCut.IsChecked == true,
            LowCutHz: SldLowCut.Value,
            HighCutHz: SldHighCut.Value,
            LimiterEnabled: ChkLimiter.IsChecked == true,
            LimitDb: SldLimit.Value,
            LoudnessEnabled: ChkLoudness.IsChecked == true,
            TargetLufs: SldLufs.Value,
            WarmthEnabled: ChkWarmth.IsChecked == true,
            WarmthPct: SldWarmth.Value,
            ReverbEnabled: ChkReverb.IsChecked == true,
            ReverbDelayMs: (int)SldDelay.Value,
            ReverbDecayPct: SldDecay.Value,
            StereoEnabled: ChkStereo.IsChecked == true,
            StereoPct: SldStereo.Value,
            PitchEnabled: ChkPitch.IsChecked == true,
            PitchSemitones: (int)SldPitch.Value,
            TrimSilenceEnabled: ChkTrimSilence.IsChecked == true,
            TrimThresholdDb: SldTrimDb.Value,
            FadeOutEnabled: ChkFadeOut.IsChecked == true,
            FadeOutSeconds: SldFadeOut.Value);
    }

    private async void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is not AudioTaskItem item) return;
        ReleasePlayers();
        _currentItem = item;
        NowPlayingText.Text = item.FileName;
        TimeText.Text = "00:00 / 00:00";
        Playhead.Margin = new Thickness(0);

        if (File.Exists(item.FilePath))
            _rawPlayer.Open(new Uri(item.FilePath));

        string? afterPath = (!string.IsNullOrEmpty(item.PreviewPath) && File.Exists(item.PreviewPath))
            ? item.PreviewPath
            : (!string.IsNullOrEmpty(item.OutputPath) && File.Exists(item.OutputPath) ? item.OutputPath : null);

        if (afterPath != null)
        {
            _masterPlayer.Open(new Uri(afterPath));
            BtnAfter.IsChecked = true;
        }

        await LoadWaveformsAndLivePreviewAsync(item);
    }

    private async Task LoadWaveformsAndLivePreviewAsync(AudioTaskItem item)
    {
        if (TxtGrade != null && item.RawQc == null)
        {
            TxtGrade.Text = "SCANNING...";
            TxtGrade.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7C85F2"));
        }

        const int barCount = 150;

        if (item.RawPcm44k == null && File.Exists(item.FilePath))
        {
            string ffmpeg;
            try
            {
                ffmpeg = await EnsureFFmpegAsync();
            }
            catch
            {
                return;
            }

            await Task.Run(() =>
            {
                short[] pcm = DecodeAudioToPcm44k(ffmpeg, item.FilePath);
                item.RawPcm44k = pcm;
                var (peaks, maxPeak, avgRms) = ComputePeaksFromPcm(pcm, barCount);
                item.RawPeaks = peaks;
                item.RawPeakLinear = maxPeak;
                item.RawRmsLinear = avgRms;
                item.RawQc = ComputeQcFromPcm(pcm, 44100);
            });
        }

        if (_currentItem != item) return;

        DrawCurrentWaveform();
        UpdateCurrentQcDashboard();

        await RenderLivePreviewForItemAsync(item, switchToAfter: true);
    }

    private async Task RenderLivePreviewForItemAsync(AudioTaskItem item, bool switchToAfter)
    {
        if (item.RawPcm44k == null || item.RawPcm44k.Length == 0) return;

        int myVersion = ++_previewVersion;
        int mySlot = (_previewFileSlot = (_previewFileSlot + 1) % 6);
        LiveFxSettings fx = CaptureCurrentFxSettings();

        string previewDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NoJ_WAV_Master",
            "preview");
        Directory.CreateDirectory(previewDir);
        string previewWavPath = System.IO.Path.Combine(previewDir, $"live_preview_{mySlot}.wav");

        const int barCount = 150;
        float[]? newPeaks = null;
        float newMaxPeak = 0f;
        float newAvgRms = 0f;
        AudioQcReport newQc = default;
        bool writeOk = false;

        await Task.Run(() =>
        {
            short[] processed = LiveDspEngine.ProcessPcm(item.RawPcm44k, fx);
            if (myVersion != _previewVersion) return;

            var (peaks, maxPeak, avgRms) = ComputePeaksFromPcm(processed, barCount);
            newPeaks = peaks;
            newMaxPeak = maxPeak;
            newAvgRms = avgRms;
            newQc = ComputeQcFromPcm(processed, 44100);
            writeOk = WriteWav16Stereo(previewWavPath, processed, 44100);
        });

        if (myVersion != _previewVersion || _currentItem != item || !writeOk || newPeaks == null)
            return;

        item.PreviewPath = previewWavPath;
        item.MasterPeaks = newPeaks;
        item.MasterPeakLinear = newMaxPeak;
        item.MasterRmsLinear = newAvgRms;
        item.MasterQc = newQc;

        if (string.IsNullOrEmpty(item.OutputPath))
        {
            item.Status = "Live FX Ready";
            item.StatusColor = "#60A5FA";
        }

        HotSwapMasterPlayer(item, previewWavPath, myVersion, switchToAfter);

        DrawCurrentWaveform();
        UpdateCurrentQcDashboard();

        if (!_isProcessing)
        {
            StatusText.Text = _settingsDirty
                ? "⚡ Live FX preview active · Click Start below to export files"
                : "⚡ Live FX preview active · Space: Play · Tab/B: Before/After";
        }
    }

    private void HotSwapMasterPlayer(AudioTaskItem item, string previewWavPath, int version, bool switchToAfter)
    {
        var nextPlayer = new MediaPlayer();
        EventHandler? openedHandler = null;
        openedHandler = (_, _) =>
        {
            nextPlayer.MediaOpened -= openedHandler;
            if (_currentItem != item || version != _previewVersion)
            {
                nextPlayer.Close();
                return;
            }

            var refPlayer = ActivePlayer;
            double ratio = 0;
            if (refPlayer.NaturalDuration.HasTimeSpan && refPlayer.NaturalDuration.TimeSpan.TotalSeconds > 0)
            {
                ratio = Math.Clamp(refPlayer.Position.TotalSeconds / refPlayer.NaturalDuration.TimeSpan.TotalSeconds, 0, 1);
            }

            if (nextPlayer.NaturalDuration.HasTimeSpan)
            {
                nextPlayer.Position = TimeSpan.FromSeconds(ratio * nextPlayer.NaturalDuration.TimeSpan.TotalSeconds);
            }

            var oldMaster = _masterPlayer;
            oldMaster.MediaOpened -= Player_MediaOpened;
            oldMaster.MediaEnded -= Player_MediaEnded;

            _masterPlayer = nextPlayer;
            _masterPlayer.MediaOpened += Player_MediaOpened;
            _masterPlayer.MediaEnded += Player_MediaEnded;

            if (switchToAfter && BtnAfter.IsChecked != true)
            {
                BtnAfter.IsChecked = true;
            }

            if (_isPlaying && BtnAfter.IsChecked == true)
            {
                _masterPlayer.Play();
                _rawPlayer.Pause();
                oldMaster.Pause();
            }

            oldMaster.Close();
            DrawCurrentWaveform();
            UpdateCurrentQcDashboard();
            UpdateTimeAndPlayhead();
        };

        nextPlayer.MediaOpened += openedHandler;
        nextPlayer.Open(new Uri(previewWavPath));
    }

    private static void CleanupPreviewTempFiles()
    {
        try
        {
            string previewDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NoJ_WAV_Master",
                "preview");
            if (Directory.Exists(previewDir))
            {
                foreach (string f in Directory.EnumerateFiles(previewDir, "live_preview_*.wav"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    private void UpdateCurrentQcDashboard()
    {
        if (_currentItem == null || TxtLufs == null)
        {
            ResetQcDisplay();
            return;
        }

        bool isAfter = BtnAfter.IsChecked == true && HasAfterAudio(_currentItem);
        AudioQcReport? report = isAfter ? _currentItem.MasterQc : _currentItem.RawQc;

        if (report == null)
        {
            TxtGrade.Text = "SCANNING...";
            TxtGrade.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7C85F2"));
            return;
        }

        var qc = report.Value;
        if (!qc.Valid)
        {
            ResetQcDisplay();
            return;
        }

        TxtLufs.Text = $"{qc.Lufs:0.0} LUFS";
        TxtPeak.Text = $"{qc.Peak:0.0} dBTP";
        TxtLra.Text = $"{qc.Lra:0.0} LU · {qc.SampleRate / 1000.0:0.#}k";

        bool peakSafe = qc.Peak <= -0.8;
        bool peakClip = qc.Peak > -0.2;
        bool lufsOptimal = qc.Lufs >= -16.5 && qc.Lufs <= -8.5;

        TxtPeak.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            peakClip ? "#F87171" : (peakSafe ? "#4ADE80" : "#FBBF24")));

        TxtLufs.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            lufsOptimal ? "#4ADE80" : "#FBBF24"));

        if (peakClip)
        {
            TxtGrade.Text = "C · CLIP RISK";
            TxtGrade.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F87171"));
            GradeBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F87171"));
        }
        else if (peakSafe && lufsOptimal)
        {
            TxtGrade.Text = "S · STUDIO PASS";
            TxtGrade.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4ADE80"));
            GradeBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4ADE80"));
        }
        else if (!peakClip && (qc.Lufs >= -20.0 && qc.Lufs <= -6.5))
        {
            TxtGrade.Text = "A · STREAM READY";
            TxtGrade.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#60A5FA"));
            GradeBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#60A5FA"));
        }
        else
        {
            TxtGrade.Text = "B · LEVEL WARN";
            TxtGrade.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FBBF24"));
            GradeBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FBBF24"));
        }
    }

    private void Player_MediaOpened(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, ActivePlayer))
            UpdateTimeAndPlayhead();
    }

    private void Player_MediaEnded(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, ActivePlayer)) return;
        ActivePlayer.Stop();
        ActivePlayer.Position = TimeSpan.Zero;
        _isPlaying = false;
        _timer.Stop();
        UpdatePlayIcon();
        Playhead.Visibility = Visibility.Collapsed;
        UpdateTimeAndPlayhead();
    }

    private void AB_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _currentItem == null) return;

        if (BtnAfter.IsChecked == true && !HasAfterAudio(_currentItem))
        {
            BtnBefore.IsChecked = true;
            return;
        }

        double ratio = 0;
        if (InactivePlayer.NaturalDuration.HasTimeSpan && InactivePlayer.NaturalDuration.TimeSpan.TotalSeconds > 0)
        {
            ratio = Math.Clamp(InactivePlayer.Position.TotalSeconds / InactivePlayer.NaturalDuration.TimeSpan.TotalSeconds, 0, 1);
        }
        InactivePlayer.Pause();

        if (ActivePlayer.NaturalDuration.HasTimeSpan)
            ActivePlayer.Position = TimeSpan.FromSeconds(ratio * ActivePlayer.NaturalDuration.TimeSpan.TotalSeconds);

        if (_isPlaying)
            ActivePlayer.Play();

        DrawCurrentWaveform();
        UpdateCurrentQcDashboard();
        UpdateTimeAndPlayhead();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem == null) return;
        if (_isPlaying)
        {
            ActivePlayer.Pause();
            InactivePlayer.Pause();
            _isPlaying = false;
            _timer.Stop();
            UpdatePlayIcon();
        }
        else
        {
            if (ActivePlayer.NaturalDuration.HasTimeSpan &&
                ActivePlayer.Position >= ActivePlayer.NaturalDuration.TimeSpan - TimeSpan.FromMilliseconds(50))
            {
                ActivePlayer.Position = TimeSpan.Zero;
            }
            ActivePlayer.Play();
            _isPlaying = true;
            Playhead.Visibility = Visibility.Visible;
            _timer.Start();
            UpdatePlayIcon();
        }
    }

    private void ReleasePlayers()
    {
        _isPlaying = false;
        _timer.Stop();
        _rawPlayer.Stop();
        _rawPlayer.Close();
        _masterPlayer.Stop();
        _masterPlayer.Close();
        Playhead.Visibility = Visibility.Collapsed;
        UpdatePlayIcon();
    }

    private void UpdatePlayIcon()
    {
        PlayPauseBtn.Tag = _isPlaying ? PauseGeometry : PlayGeometry;
    }

    private void Timer_Tick(object? sender, EventArgs e) => UpdateTimeAndPlayhead();

    private void UpdateTimeAndPlayhead()
    {
        var player = ActivePlayer;
        if (!player.NaturalDuration.HasTimeSpan) return;
        var total = player.NaturalDuration.TimeSpan;
        var pos = player.Position;
        TimeText.Text = $"{pos:mm\\:ss} / {total:mm\\:ss}";

        double ratio = total.TotalSeconds > 0 ? pos.TotalSeconds / total.TotalSeconds : 0;
        double canvasWidth = WaveformCanvas.ActualWidth;
        Playhead.Margin = new Thickness(Math.Clamp(ratio * canvasWidth, 0, Math.Max(0, canvasWidth - 2)), 0, 0, 0);
    }

    private void Waveform_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var player = ActivePlayer;
        if (_currentItem == null || !player.NaturalDuration.HasTimeSpan || WaveformCanvas.ActualWidth <= 0) return;
        var pt = e.GetPosition(WaveformCanvas);
        double ratio = Math.Clamp(pt.X / WaveformCanvas.ActualWidth, 0, 1);
        player.Position = TimeSpan.FromSeconds(player.NaturalDuration.TimeSpan.TotalSeconds * ratio);
        Playhead.Visibility = Visibility.Visible;
        UpdateTimeAndPlayhead();
    }

    private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_currentItem != null)
        {
            DrawCurrentWaveform();
            UpdateTimeAndPlayhead();
        }
    }

    private static short[] DecodeAudioToPcm44k(string ffmpegPath, string filePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-v error -i \"{filePath}\" -vn -ac 2 -ar 44100 -f s16le -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return [];

            using var ms = new MemoryStream();
            proc.StandardOutput.BaseStream.CopyTo(ms);
            proc.WaitForExit();

            byte[] bytes = ms.ToArray();
            int sampleCount = bytes.Length / 2;
            short[] samples = new short[sampleCount];
            Buffer.BlockCopy(bytes, 0, samples, 0, sampleCount * 2);
            return samples;
        }
        catch
        {
            return [];
        }
    }

    private static bool WriteWav16Stereo(string outPath, short[] samples, int sampleRate)
    {
        try
        {
            int dataBytes = samples.Length * 2;
            using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read, 65536);
            using var bw = new BinaryWriter(fs);

            bw.Write("RIFF"u8);
            bw.Write(36 + dataBytes);
            bw.Write("WAVE"u8);
            bw.Write("fmt "u8);
            bw.Write(16);
            bw.Write((short)1); // PCM
            bw.Write((short)2); // 2 channels
            bw.Write(sampleRate);
            bw.Write(sampleRate * 4); // byteRate
            bw.Write((short)4); // blockAlign
            bw.Write((short)16); // bitsPerSample
            bw.Write("data"u8);
            bw.Write(dataBytes);

            byte[] rawBytes = new byte[dataBytes];
            Buffer.BlockCopy(samples, 0, rawBytes, 0, dataBytes);
            bw.Write(rawBytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (float[] peaks, float trackMaxPeak, float trackAvgRms) ComputePeaksFromPcm(short[] samples, int bars)
    {
        var result = new float[bars];
        int stereoFrames = samples.Length / 2;
        if (stereoFrames == 0) return (result, 0f, 0f);

        float globalMaxPeak = 0f;
        double globalSumSq = 0;
        long totalFrames = 0;

        int step = Math.Max(1, stereoFrames / bars);
        for (int b = 0; b < bars; b++)
        {
            int start = b * step;
            float maxPeak = 0f;
            double sumSq = 0;
            int count = 0;

            for (int i = 0; i < step && (start + i) < stereoFrames; i++)
            {
                int idx = (start + i) * 2;
                float left = samples[idx] / 32768f;
                float right = samples[idx + 1] / 32768f;

                float absL = Math.Abs(left);
                float absR = Math.Abs(right);
                float framePeak = Math.Max(absL, absR);
                float sideEnergy = Math.Abs(left - right) * 0.50f;
                float effectiveSample = framePeak + sideEnergy * 0.32f;

                if (effectiveSample > maxPeak) maxPeak = effectiveSample;
                if (framePeak > globalMaxPeak) globalMaxPeak = framePeak;

                double frameSq = (left * left + right * right) * 0.5;
                sumSq += frameSq;
                globalSumSq += frameSq;
                count++;
                totalFrames++;
            }

            float rms = count > 0 ? (float)Math.Sqrt(sumSq / count) : 0f;
            float combined = 0.52f * maxPeak + 0.48f * (rms * 1.95f);
            result[b] = Math.Clamp(combined, 0.02f, 2.5f);
        }

        float avgRms = totalFrames > 0 ? (float)Math.Sqrt(globalSumSq / totalFrames) : 0f;
        return (result, globalMaxPeak, avgRms);
    }

    private static AudioQcReport ComputeQcFromPcm(short[] samples, int sampleRate)
    {
        int stereoFrames = samples.Length / 2;
        if (stereoFrames == 0) return new AudioQcReport(0, 0, 0, sampleRate, false);

        float maxPeak = 0f;
        int blockFrames = Math.Max(1, (int)(sampleRate * 0.4)); // 400ms EBU momentary blocks
        int blockCount = Math.Max(1, stereoFrames / blockFrames);
        var blockLufs = new List<double>(blockCount);
        double totalEnergy = 0;
        long activeFrames = 0;

        for (int blk = 0; blk < blockCount; blk++)
        {
            int start = blk * blockFrames;
            int end = Math.Min(stereoFrames, start + blockFrames);
            double blkSum = 0;
            for (int f = start; f < end; f++)
            {
                float l = samples[f * 2] / 32768f;
                float r = samples[f * 2 + 1] / 32768f;
                float pk = Math.Max(Math.Abs(l), Math.Abs(r));
                if (pk > maxPeak) maxPeak = pk;
                blkSum += (l * l + r * r);
            }

            int len = end - start;
            if (len > 0)
            {
                double meanSq = blkSum / len;
                double lufs = -0.691 + 10.0 * Math.Log10(meanSq + 1e-12);
                if (lufs > -70.0)
                {
                    blockLufs.Add(lufs);
                    totalEnergy += blkSum;
                    activeFrames += len;
                }
            }
        }

        double integratedLufs = activeFrames > 0
            ? -0.691 + 10.0 * Math.Log10((totalEnergy / activeFrames) + 1e-12)
            : -70.0;

        double peakDb = maxPeak > 0.00001f ? 20.0 * Math.Log10(maxPeak) : -96.0;

        double lra = 6.0;
        if (blockLufs.Count >= 4)
        {
            blockLufs.Sort();
            double p10 = blockLufs[(int)(blockLufs.Count * 0.10)];
            double p95 = blockLufs[(int)(blockLufs.Count * 0.95)];
            lra = Math.Clamp(p95 - p10, 0.5, 30.0);
        }

        return new AudioQcReport(integratedLufs, peakDb, lra, sampleRate, true);
    }

    private static double LinearToDb(float linear)
    {
        if (linear <= 0.00001f) return -96.0;
        return 20.0 * Math.Log10(linear);
    }

    private void DrawCurrentWaveform()
    {
        WaveformCanvas.Children.Clear();
        if (_currentItem == null) return;

        float[]? raw = _currentItem.RawPeaks;
        float[]? master = _currentItem.MasterPeaks;
        if (raw == null || raw.Length == 0) return;

        double w = WaveformCanvas.ActualWidth > 0 ? WaveformCanvas.ActualWidth : 488;
        double h = WaveformCanvas.ActualHeight > 0 ? WaveformCanvas.ActualHeight : 98;
        double centerY = h / 2.0;

        var gridBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#151924"));
        foreach (double offsetRatio in new[] { 0.38, -0.38, 0.76, -0.76 })
        {
            double gy = centerY + offsetRatio * (h / 2.0);
            WaveformCanvas.Children.Add(new Line
            {
                X1 = 0,
                Y1 = gy,
                X2 = w,
                Y2 = gy,
                Stroke = gridBrush,
                StrokeThickness = 1,
                StrokeDashArray = [3, 3]
            });
        }

        var centerLine = new Line
        {
            X1 = 0,
            Y1 = centerY,
            X2 = w,
            Y2 = centerY,
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202636")),
            StrokeThickness = 1
        };
        WaveformCanvas.Children.Add(centerLine);

        int count = raw.Length;
        double barSlot = w / count;
        double barWidth = Math.Max(2.0, barSlot - 1.1);
        double maxDrawH = Math.Max(20, h - 6);

        bool hasMaster = master != null && master.Length == count;
        bool isAfterSelected = BtnAfter.IsChecked == true && hasMaster;

        if (!hasMaster)
        {
            float rawMax = Math.Max(0.15f, raw.Max());
            var rawBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F472B6"));

            for (int i = 0; i < count; i++)
            {
                double norm = Math.Pow(Math.Clamp(raw[i] / rawMax, 0.03, 1.0), 0.80) * 0.80;
                double barH = Math.Max(4.0, norm * maxDrawH);
                var rect = new Rectangle
                {
                    Width = barWidth,
                    Height = barH,
                    RadiusX = 1.2,
                    RadiusY = 1.2,
                    Fill = rawBrush,
                    Opacity = 0.82
                };
                Canvas.SetLeft(rect, i * barSlot + 0.5);
                Canvas.SetTop(rect, centerY - barH / 2.0);
                WaveformCanvas.Children.Add(rect);
            }

            double rawPeakDb = LinearToDb(_currentItem.RawPeakLinear);
            double rawRmsDb = LinearToDb(_currentItem.RawRmsLinear);
            DiffStatsText.Text = $"Raw Preview · Peak {rawPeakDb:0.0} dB · RMS {rawRmsDb:0.0} dB";
            return;
        }

        float[] masterArr = master!;
        // Anchor scale to Raw peak with 22% headroom so After boosts visibly rise above Raw instead of compressing Raw down
        float rawRefMax = Math.Max(0.15f, raw.Max());
        float masterRefMax = Math.Max(0.15f, masterArr.Max());
        float sharedScale = Math.Max(rawRefMax * 1.22f, masterRefMax * 1.04f);

        var baseBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isAfterSelected ? "#323A52" : "#3A3248"));
        var boostBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4ADE80"));
        var cutBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F472B6"));

        int boostedBars = 0;
        int cutBars = 0;

        for (int i = 0; i < count; i++)
        {
            double rNorm = Math.Pow(Math.Clamp(raw[i] / sharedScale, 0.02, 1.0), 0.78);
            double mNorm = Math.Pow(Math.Clamp(masterArr[i] / sharedScale, 0.02, 1.0), 0.78);

            double rawH = Math.Max(4.0, rNorm * maxDrawH);
            double masterH = Math.Max(4.0, mNorm * maxDrawH);

            double baseH = Math.Min(rawH, masterH);
            double rawDelta = masterArr[i] - raw[i];
            double absDelta = Math.Abs(rawDelta);

            double visualDiffH = 0;
            if (absDelta >= 0.0015)
            {
                double relDelta = Math.Clamp(absDelta / rawRefMax, 0.0, 1.2);
                double magnified = Math.Pow(relDelta, 0.58) * maxDrawH * 0.58;
                visualDiffH = Math.Clamp(Math.Max(7.0, magnified), 7.0, maxDrawH - 4.0);
                if (baseH + visualDiffH > maxDrawH)
                    baseH = Math.Max(4.0, maxDrawH - visualDiffH);
            }

            double x = i * barSlot + 0.5;

            if (visualDiffH > 0)
            {
                bool isBoost = rawDelta > 0;
                if (isBoost) boostedBars++;
                else cutBars++;

                double outerH = baseH + visualDiffH;
                var diffRect = new Rectangle
                {
                    Width = barWidth,
                    Height = outerH,
                    RadiusX = 1.3,
                    RadiusY = 1.3,
                    Fill = isBoost ? boostBrush : cutBrush,
                    Opacity = (isBoost == isAfterSelected) ? 1.0 : 0.90
                };
                Canvas.SetLeft(diffRect, x);
                Canvas.SetTop(diffRect, centerY - outerH / 2.0);
                WaveformCanvas.Children.Add(diffRect);
            }

            var baseRect = new Rectangle
            {
                Width = barWidth,
                Height = baseH,
                RadiusX = 1.0,
                RadiusY = 1.0,
                Fill = baseBrush,
                Opacity = 0.94
            };
            Canvas.SetLeft(baseRect, x);
            Canvas.SetTop(baseRect, centerY - baseH / 2.0);
            WaveformCanvas.Children.Add(baseRect);
        }

        double peakDeltaDb = LinearToDb(_currentItem.MasterPeakLinear) - LinearToDb(_currentItem.RawPeakLinear);
        double rmsDeltaDb = LinearToDb(_currentItem.MasterRmsLinear) - LinearToDb(_currentItem.RawRmsLinear);
        string pSign = peakDeltaDb >= 0 ? "+" : "";
        string rSign = rmsDeltaDb >= 0 ? "+" : "";

        if (boostedBars == 0 && cutBars == 0)
        {
            DiffStatsText.Text = "Identical Waveform (Bypass FX)";
        }
        else
        {
            DiffStatsText.Text = $"Peak Δ {pSign}{peakDeltaDb:0.0} dB · Energy Δ {rSign}{rmsDeltaDb:0.0} dB";
        }
    }
}

public enum EngineMode { Clean, Loud, Manual, ConvertOnly }

public readonly record struct LiveFxSettings(
    EngineMode Mode,
    bool AtmosEnabled,
    double AtmosDepth,
    double AtmosSurround,
    double AtmosLfeDb,
    bool EqEnabled,
    double LowCutHz,
    double HighCutHz,
    bool LimiterEnabled,
    double LimitDb,
    bool LoudnessEnabled,
    double TargetLufs,
    bool WarmthEnabled,
    double WarmthPct,
    bool ReverbEnabled,
    int ReverbDelayMs,
    double ReverbDecayPct,
    bool StereoEnabled,
    double StereoPct,
    bool PitchEnabled,
    int PitchSemitones,
    bool TrimSilenceEnabled,
    double TrimThresholdDb,
    bool FadeOutEnabled,
    double FadeOutSeconds);

public static class LiveDspEngine
{
    private struct Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2;
        private double _z1L, _z2L, _z1R, _z2R;

        public static Biquad CreateHighPass(double sr, double freq, double q = 0.7071)
        {
            double w0 = 2.0 * Math.PI * Math.Clamp(freq, 10.0, sr * 0.48) / sr;
            double cosW = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2.0 * q);
            double a0 = 1.0 + alpha;
            return new Biquad
            {
                _b0 = ((1.0 + cosW) * 0.5) / a0,
                _b1 = -(1.0 + cosW) / a0,
                _b2 = ((1.0 + cosW) * 0.5) / a0,
                _a1 = (-2.0 * cosW) / a0,
                _a2 = (1.0 - alpha) / a0
            };
        }

        public static Biquad CreateLowPass(double sr, double freq, double q = 0.7071)
        {
            double w0 = 2.0 * Math.PI * Math.Clamp(freq, 100.0, sr * 0.49) / sr;
            double cosW = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2.0 * q);
            double a0 = 1.0 + alpha;
            return new Biquad
            {
                _b0 = ((1.0 - cosW) * 0.5) / a0,
                _b1 = (1.0 - cosW) / a0,
                _b2 = ((1.0 - cosW) * 0.5) / a0,
                _a1 = (-2.0 * cosW) / a0,
                _a2 = (1.0 - alpha) / a0
            };
        }

        public static Biquad CreateLowShelf(double sr, double freq, double gainDb, double q = 0.7071)
        {
            double a = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * freq / sr;
            double cosW = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2.0 * q);
            double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;

            double a0 = (a + 1.0) + (a - 1.0) * cosW + twoSqrtAAlpha;
            return new Biquad
            {
                _b0 = (a * ((a + 1.0) - (a - 1.0) * cosW + twoSqrtAAlpha)) / a0,
                _b1 = (2.0 * a * ((a - 1.0) - (a + 1.0) * cosW)) / a0,
                _b2 = (a * ((a + 1.0) - (a - 1.0) * cosW - twoSqrtAAlpha)) / a0,
                _a1 = (-2.0 * ((a - 1.0) + (a + 1.0) * cosW)) / a0,
                _a2 = ((a + 1.0) + (a - 1.0) * cosW - twoSqrtAAlpha) / a0
            };
        }

        public static Biquad CreateHighShelf(double sr, double freq, double gainDb, double q = 0.7071)
        {
            double a = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * freq / sr;
            double cosW = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2.0 * q);
            double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;

            double a0 = (a + 1.0) - (a - 1.0) * cosW + twoSqrtAAlpha;
            return new Biquad
            {
                _b0 = (a * ((a + 1.0) + (a - 1.0) * cosW + twoSqrtAAlpha)) / a0,
                _b1 = (-2.0 * a * ((a - 1.0) + (a + 1.0) * cosW)) / a0,
                _b2 = (a * ((a + 1.0) + (a - 1.0) * cosW - twoSqrtAAlpha)) / a0,
                _a1 = (2.0 * ((a - 1.0) - (a + 1.0) * cosW)) / a0,
                _a2 = ((a + 1.0) - (a - 1.0) * cosW - twoSqrtAAlpha) / a0
            };
        }

        public void ProcessStereo(ref float l, ref float r)
        {
            double outL = _b0 * l + _z1L;
            _z1L = _b1 * l - _a1 * outL + _z2L;
            _z2L = _b2 * l - _a2 * outL;
            l = (float)outL;

            double outR = _b0 * r + _z1R;
            _z1R = _b1 * r - _a1 * outR + _z2R;
            _z2R = _b2 * r - _a2 * outR;
            r = (float)outR;
        }
    }

    public static short[] ProcessPcm(short[] rawPcm, LiveFxSettings fx)
    {
        if (rawPcm.Length < 4 || (fx.Mode == EngineMode.ConvertOnly && !fx.TrimSilenceEnabled && !fx.FadeOutEnabled))
        {
            var copy = new short[rawPcm.Length];
            Buffer.BlockCopy(rawPcm, 0, copy, 0, rawPcm.Length * 2);
            return copy;
        }

        const double sr = 44100.0;
        int inFrames = rawPcm.Length / 2;

        bool doPitch = fx.Mode == EngineMode.Manual && fx.PitchEnabled && fx.PitchSemitones != 0;
        double pitchFactor = doPitch ? Math.Pow(2.0, fx.PitchSemitones / 12.0) : 1.0;
        int outFrames = doPitch ? Math.Max(1, (int)(inFrames / pitchFactor)) : inFrames;

        float[] buf = new float[outFrames * 2];

        bool doEq = fx.Mode is EngineMode.Clean or EngineMode.Loud || (fx.Mode == EngineMode.Manual && fx.EqEnabled);
        double hpFreq = fx.Mode == EngineMode.Manual ? fx.LowCutHz : 20.0;
        double lpFreq = fx.Mode == EngineMode.Manual ? fx.HighCutHz : 18000.0;
        Biquad hp = Biquad.CreateHighPass(sr, hpFreq);
        Biquad lp = Biquad.CreateLowPass(sr, lpFreq);

        bool doWarmth = fx.Mode == EngineMode.Manual && fx.WarmthEnabled && fx.WarmthPct > 0;
        float warmthDrive = (float)(1.0 + (fx.WarmthPct / 100.0) * 1.9);
        float warmthNorm = (float)(1.0 / Math.Tanh(warmthDrive));
        float warmthMakeup = (float)(1.0 + (fx.WarmthPct / 100.0) * 0.45);

        bool doAtmos = fx.Mode == EngineMode.Manual && fx.AtmosEnabled;
        bool doAtmosLfe = doAtmos && fx.AtmosLfeDb > 0.05;
        Biquad atmosLfe = Biquad.CreateLowShelf(sr, 60.0, fx.AtmosLfeDb);

        bool doAtmosDepth = doAtmos && fx.AtmosDepth > 0;
        double depthNorm = fx.AtmosDepth / 100.0;
        int haasDelayL = Math.Max(1, (int)(sr * (1.6 + depthNorm * 5.2) / 1000.0));
        int haasDelayR = Math.Max(1, (int)(sr * (2.1 + depthNorm * 6.4) / 1000.0));
        int haasMax = Math.Max(haasDelayL, haasDelayR) + 4;
        float[] haasBufL = doAtmosDepth ? new float[haasMax] : [];
        float[] haasBufR = doAtmosDepth ? new float[haasMax] : [];
        int haasIdx = 0;
        float haasSideMix = (float)(0.22 + depthNorm * 0.36);
        Biquad atmosAir = Biquad.CreateHighShelf(sr, 11000.0, depthNorm * 2.8);

        bool doAtmosSurround = doAtmos && fx.AtmosSurround > 100;
        double surrNorm = (fx.AtmosSurround - 100.0) / 120.0;
        float atmosSlev = (float)(1.0 + surrNorm * 0.95);
        float atmosBase = (float)Math.Clamp(surrNorm * 0.35, 0.0, 0.55);

        bool doStereo = fx.Mode == EngineMode.Manual && fx.StereoEnabled && fx.StereoPct > 100;
        float stereoMlev = (float)(1.0 + ((fx.StereoPct - 100.0) / 100.0) * 0.8);

        bool doReverb = fx.Mode == EngineMode.Manual && fx.ReverbEnabled;
        int revDelay1 = Math.Max(1, (int)(sr * Math.Clamp(fx.ReverbDelayMs, 20, 300) / 1000.0));
        int revDelay2 = Math.Max(1, (int)(revDelay1 * 0.63));
        int revBufLen = revDelay1 + 4;
        float[] revBufL = doReverb ? new float[revBufLen] : [];
        float[] revBufR = doReverb ? new float[revBufLen] : [];
        int revIdx = 0;
        float revDecay = (float)Math.Clamp(fx.ReverbDecayPct / 100.0, 0.05, 0.78);

        double energySum = 0;
        long energyFrames = 0;

        for (int i = 0; i < outFrames; i++)
        {
            float l, r;
            if (doPitch)
            {
                double srcPos = i * pitchFactor;
                int idx0 = Math.Min(inFrames - 1, (int)srcPos);
                int idx1 = Math.Min(inFrames - 1, idx0 + 1);
                float frac = (float)(srcPos - idx0);
                l = (rawPcm[idx0 * 2] * (1f - frac) + rawPcm[idx1 * 2] * frac) / 32768f;
                r = (rawPcm[idx0 * 2 + 1] * (1f - frac) + rawPcm[idx1 * 2 + 1] * frac) / 32768f;
            }
            else
            {
                l = rawPcm[i * 2] / 32768f;
                r = rawPcm[i * 2 + 1] / 32768f;
            }

            if (doEq)
            {
                hp.ProcessStereo(ref l, ref r);
                lp.ProcessStereo(ref l, ref r);
            }

            if (doWarmth)
            {
                l = (float)Math.Tanh(l * warmthDrive) * warmthNorm * warmthMakeup;
                r = (float)Math.Tanh(r * warmthDrive) * warmthNorm * warmthMakeup;
            }

            if (doAtmos)
            {
                if (doAtmosLfe)
                    atmosLfe.ProcessStereo(ref l, ref r);

                if (doAtmosDepth)
                {
                    haasBufL[haasIdx] = l;
                    haasBufR[haasIdx] = r;
                    int readL = (haasIdx - haasDelayL + haasMax) % haasMax;
                    int readR = (haasIdx - haasDelayR + haasMax) % haasMax;
                    float delayedL = haasBufL[readL];
                    float delayedR = -haasBufR[readR]; // opposite phase right Haas spatializer
                    haasIdx = (haasIdx + 1) % haasMax;

                    float mid = (l + r) * 0.5f;
                    l = mid + (l - r) * 0.5f + delayedL * haasSideMix;
                    r = mid + (r - l) * 0.5f + delayedR * haasSideMix;
                    atmosAir.ProcessStereo(ref l, ref r);
                }

                if (doAtmosSurround)
                {
                    float mid = (l + r) * 0.5f;
                    float side = (l - r) * 0.5f * atmosSlev;
                    l = mid + side - r * atmosBase * 0.25f;
                    r = mid - side - l * atmosBase * 0.25f;
                }
            }

            if (doStereo)
            {
                float mid = (l + r) * 0.5f;
                float side = (l - r) * 0.5f * stereoMlev;
                l = mid + side;
                r = mid - side;
            }

            if (doReverb)
            {
                int r1 = (revIdx - revDelay1 + revBufLen) % revBufLen;
                int r2 = (revIdx - revDelay2 + revBufLen) % revBufLen;
                float echoL = revBufL[r1] * revDecay + revBufR[r2] * (revDecay * 0.45f);
                float echoR = revBufR[r1] * revDecay + revBufL[r2] * (revDecay * 0.45f);
                revBufL[revIdx] = l + echoL * 0.35f;
                revBufR[revIdx] = r + echoR * 0.35f;
                revIdx = (revIdx + 1) % revBufLen;
                l = l * 0.86f + echoL;
                r = r * 0.86f + echoR;
            }

            buf[i * 2] = l;
            buf[i * 2 + 1] = r;

            double sq = l * l + r * r;
            if (sq > 1e-7)
            {
                energySum += sq;
                energyFrames++;
            }
        }

        bool doLoudness = fx.Mode == EngineMode.Loud || (fx.Mode == EngineMode.Manual && fx.LoudnessEnabled);
        float loudnessGain = 1.0f;
        if (doLoudness && energyFrames > 0)
        {
            double targetLufs = fx.Mode == EngineMode.Loud ? -14.0 : fx.TargetLufs;
            double currentLufs = -0.691 + 10.0 * Math.Log10((energySum / energyFrames) + 1e-12);
            double deltaDb = Math.Clamp(targetLufs - currentLufs, -18.0, 18.0);
            loudnessGain = (float)Math.Pow(10.0, deltaDb / 20.0);
        }

        bool doLimiter = fx.Mode is EngineMode.Clean or EngineMode.Loud || (fx.Mode == EngineMode.Manual && fx.LimiterEnabled);
        double limitDb = fx.Mode == EngineMode.Manual ? fx.LimitDb : -1.0;
        float ceiling = doLimiter ? (float)Math.Pow(10.0, limitDb / 20.0) : 0.999f;

        short[] output = new short[outFrames * 2];
        float envGain = 1.0f;
        const float releaseCoeff = 0.9992f;

        for (int i = 0; i < outFrames; i++)
        {
            float l = buf[i * 2] * loudnessGain;
            float r = buf[i * 2 + 1] * loudnessGain;

            if (doLimiter)
            {
                float pk = Math.Max(Math.Abs(l), Math.Abs(r));
                if (pk * envGain > ceiling && pk > 0.0001f)
                {
                    envGain = ceiling / pk;
                }
                else
                {
                    envGain = Math.Min(1.0f, envGain / releaseCoeff);
                }
                l = Math.Clamp(l * envGain, -ceiling, ceiling);
                r = Math.Clamp(r * envGain, -ceiling, ceiling);
            }
            else
            {
                l = Math.Clamp(l, -1.0f, 1.0f);
                r = Math.Clamp(r, -1.0f, 1.0f);
            }

            output[i * 2] = (short)Math.Clamp((int)(l * 32767f), short.MinValue, short.MaxValue);
            output[i * 2 + 1] = (short)Math.Clamp((int)(r * 32767f), short.MinValue, short.MaxValue);
        }

        int startFrame = 0;
        int endFrame = outFrames - 1;

        if (fx.TrimSilenceEnabled && outFrames > 200)
        {
            short threshShort = (short)Math.Clamp((int)(Math.Pow(10.0, fx.TrimThresholdDb / 20.0) * 32767.0), 4, 16000);
            int firstActive = 0;
            while (firstActive < outFrames)
            {
                int absL = Math.Abs((int)output[firstActive * 2]);
                int absR = Math.Abs((int)output[firstActive * 2 + 1]);
                if (absL >= threshShort || absR >= threshShort) break;
                firstActive++;
            }

            int lastActive = outFrames - 1;
            while (lastActive > firstActive)
            {
                int absL = Math.Abs((int)output[lastActive * 2]);
                int absR = Math.Abs((int)output[lastActive * 2 + 1]);
                if (absL >= threshShort || absR >= threshShort) break;
                lastActive--;
            }

            if (firstActive < lastActive)
            {
                int preRoll = (int)(sr * 0.012);  // 12ms pre-roll
                int postRoll = (int)(sr * 0.035); // 35ms post-roll
                startFrame = Math.Max(0, firstActive - preRoll);
                endFrame = Math.Min(outFrames - 1, lastActive + postRoll);
            }
        }

        int finalFrames = Math.Max(1, endFrame - startFrame + 1);
        if (startFrame > 0 || endFrame < outFrames - 1)
        {
            short[] trimmed = new short[finalFrames * 2];
            Buffer.BlockCopy(output, startFrame * 4, trimmed, 0, finalFrames * 4);
            output = trimmed;

            // Smooth 12ms micro fade-in at trimmed start to prevent clicks
            int fadeInFrames = Math.Min(finalFrames / 4, (int)(sr * 0.012));
            for (int f = 0; f < fadeInFrames; f++)
            {
                double gain = Math.Sin(0.5 * Math.PI * (f / (double)fadeInFrames));
                output[f * 2] = (short)(output[f * 2] * gain);
                output[f * 2 + 1] = (short)(output[f * 2 + 1] * gain);
            }
        }

        if (fx.FadeOutEnabled && finalFrames > 100)
        {
            int fadeOutFrames = Math.Clamp((int)(sr * fx.FadeOutSeconds), 1, finalFrames / 2);
            int fadeStart = finalFrames - fadeOutFrames;
            for (int f = 0; f < fadeOutFrames; f++)
            {
                int frameIdx = fadeStart + f;
                double remainingRatio = (double)(fadeOutFrames - 1 - f) / fadeOutFrames;
                double gain = Math.Sin(0.5 * Math.PI * remainingRatio);
                output[frameIdx * 2] = (short)(output[frameIdx * 2] * gain);
                output[frameIdx * 2 + 1] = (short)(output[frameIdx * 2 + 1] * gain);
            }
        }

        return output;
    }
}

public readonly record struct AudioQcReport(double Lufs, double Peak, double Lra, int SampleRate, bool Valid);

public class AudioTaskItem : INotifyPropertyChanged
{
    public string FilePath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string PreviewPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public short[]? RawPcm44k { get; set; }
    public float[]? RawPeaks { get; set; }
    public float[]? MasterPeaks { get; set; }
    public float RawPeakLinear { get; set; }
    public float RawRmsLinear { get; set; }
    public float MasterPeakLinear { get; set; }
    public float MasterRmsLinear { get; set; }
    public AudioQcReport? RawQc { get; set; }
    public AudioQcReport? MasterQc { get; set; }

    private string _status = "";
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(nameof(Status)); }
    }

    private string _statusColor = "#8E93A6";
    public string StatusColor
    {
        get => _statusColor;
        set { _statusColor = value; OnPropertyChanged(nameof(StatusColor)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
