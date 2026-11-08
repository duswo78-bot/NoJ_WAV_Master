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
    private readonly MediaPlayer _masterPlayer = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly SemaphoreSlim _ffmpegLock = new(1, 1);
    private AudioTaskItem? _currentItem;
    private bool _isPlaying;
    private bool _isProcessing;
    private bool _settingsDirty;
    private bool _suppressAutoManual;
    private string? _ffmpegPath;

    private MediaPlayer ActivePlayer => (BtnAfter.IsChecked == true && _currentItem != null && !string.IsNullOrEmpty(_currentItem.OutputPath))
        ? _masterPlayer
        : _rawPlayer;
    private MediaPlayer InactivePlayer => ReferenceEquals(ActivePlayer, _masterPlayer) ? _rawPlayer : _masterPlayer;

    public MainWindow()
    {
        InitializeComponent();
        FileList.ItemsSource = Tasks;
        _timer.Tick += Timer_Tick;
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
        Close();
    }

    private void ToggleRack_Click(object sender, RoutedEventArgs e)
    {
        bool open = ToggleRackBtn.IsChecked == true;
        FxRackPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        Width = open ? 880 : 560;
    }

    private void ModeManual_Checked(object sender, RoutedEventArgs e)
    {
        if (ToggleRackBtn != null && ToggleRackBtn.IsChecked == false)
            ToggleRackBtn.IsChecked = true;
        MarkSettingsChanged();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        MarkSettingsChanged();
    }

    private void FxControl_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressAutoManual) return;
        if (ModeManual.IsChecked != true) ModeManual.IsChecked = true;
        MarkSettingsChanged();
    }

    private void FxSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _suppressAutoManual) return;
        if (ModeManual.IsChecked != true) ModeManual.IsChecked = true;
        MarkSettingsChanged();
    }

    private void MarkSettingsChanged()
    {
        if (!IsLoaded) return;
        if (Tasks.Any(t => !string.IsNullOrEmpty(t.OutputPath)))
        {
            _settingsDirty = true;
            if (!_isProcessing)
                StatusText.Text = "Settings adjusted · Click Start button below to apply";
        }
        UpdateStartButtonState();
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
        }
        finally
        {
            _suppressAutoManual = false;
        }
        MarkSettingsChanged();
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
        var (ext, codecArg, suffix) = GetFormatArguments();

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
                item.Status = $"Done ({ext.ToUpperInvariant().TrimStart('.')})";
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
        if (ModeConvert.IsChecked == true) return "";
        if (ModeClean.IsChecked == true)
            return "-af \"highpass=f=20:poles=2,lowpass=f=18000:poles=2,alimiter=limit=0.891:level=disabled:attack=5:release=50\"";
        if (ModeLoud.IsChecked == true)
            return "-af \"highpass=f=20:poles=2,lowpass=f=18000:poles=2,loudnorm=I=-14:TP=-1.0:LRA=11\"";

        var filters = new List<string>();
        var inv = CultureInfo.InvariantCulture;

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

        return filters.Count > 0 ? $"-af \"{string.Join(",", filters)}\"" : "";
    }

    private (string ext, string codec, string suffix) GetFormatArguments()
    {
        string modeSuffix = ModeConvert.IsChecked == true ? "_Converted" : "_Mastered";

        if (FmtWav24.IsChecked == true) return (".wav", "-ar 44100 -ac 2 -c:a pcm_s24le", modeSuffix);
        if (FmtWav16.IsChecked == true) return (".wav", "-ar 44100 -ac 2 -c:a pcm_s16le", modeSuffix);
        if (FmtFlac.IsChecked == true)  return (".flac", "-ar 44100 -ac 2 -c:a flac -compression_level 8", modeSuffix);
        if (FmtMp3.IsChecked == true)   return (".mp3", "-ar 44100 -ac 2 -c:a libmp3lame -b:a 320k", modeSuffix);
        if (FmtAiff.IsChecked == true)  return (".aiff", "-ar 44100 -ac 2 -c:a pcm_s24be", modeSuffix);
        if (FmtAac.IsChecked == true)   return (".m4a", "-ar 44100 -ac 2 -c:a aac -b:a 256k", modeSuffix);

        return (".wav", "-ar 44100 -ac 2 -c:a pcm_s24le", modeSuffix);
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

        bool hasMaster = !string.IsNullOrEmpty(item.OutputPath) && File.Exists(item.OutputPath);
        if (hasMaster)
        {
            _masterPlayer.Open(new Uri(item.OutputPath));
            BtnAfter.IsChecked = true;
        }
        else
        {
            BtnBefore.IsChecked = true;
        }

        await LoadWaveformsAsync(item);
    }

    private async Task LoadWaveformsAsync(AudioTaskItem item)
    {
        bool isAfter = BtnAfter.IsChecked == true && !string.IsNullOrEmpty(item.OutputPath);
        var cachedQc = isAfter ? item.MasterQc : item.RawQc;
        if (cachedQc != null)
        {
            UpdateCurrentQcDashboard();
        }
        else if (TxtGrade != null)
        {
            TxtGrade.Text = "SCANNING...";
            TxtGrade.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7C85F2"));
        }

        string ffmpeg;
        try
        {
            ffmpeg = await EnsureFFmpegAsync();
        }
        catch
        {
            return;
        }

        const int barCount = 150;
        var pendingTasks = new List<Task>();

        if (item.RawPeaks == null && File.Exists(item.FilePath))
        {
            pendingTasks.Add(Task.Run(() =>
            {
                var (peaks, maxPeak, avgRms) = ExtractWaveformData(ffmpeg, item.FilePath, barCount);
                item.RawPeaks = peaks;
                item.RawPeakLinear = maxPeak;
                item.RawRmsLinear = avgRms;
            }));
        }

        if (item.RawQc == null && File.Exists(item.FilePath))
        {
            pendingTasks.Add(Task.Run(() =>
            {
                item.RawQc = AnalyzeAudioQuality(ffmpeg, item.FilePath);
            }));
        }

        if (!string.IsNullOrEmpty(item.OutputPath) && File.Exists(item.OutputPath))
        {
            if (item.MasterPeaks == null)
            {
                pendingTasks.Add(Task.Run(() =>
                {
                    var (peaks, maxPeak, avgRms) = ExtractWaveformData(ffmpeg, item.OutputPath, barCount);
                    item.MasterPeaks = peaks;
                    item.MasterPeakLinear = maxPeak;
                    item.MasterRmsLinear = avgRms;
                }));
            }

            if (item.MasterQc == null)
            {
                pendingTasks.Add(Task.Run(() =>
                {
                    item.MasterQc = AnalyzeAudioQuality(ffmpeg, item.OutputPath);
                }));
            }
        }

        if (pendingTasks.Count > 0)
            await Task.WhenAll(pendingTasks);

        if (_currentItem == item)
        {
            DrawCurrentWaveform();
            UpdateCurrentQcDashboard();
        }
    }

    private static AudioQcReport AnalyzeAudioQuality(string ffmpegPath, string filePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-nostats -i \"{filePath}\" -vn -sn -dn -af ebur128=peak=true -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return new AudioQcReport(0, 0, 0, 44100, false);

            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            var inv = CultureInfo.InvariantCulture;
            double lufs = -14.0, peak = -1.0, lra = 7.0;
            int sr = 44100;

            var srMatch = Regex.Match(stderr, @"(\d{4,6})\s*Hz");
            if (srMatch.Success) int.TryParse(srMatch.Groups[1].Value, out sr);

            var iMatches = Regex.Matches(stderr, @"I:\s*([-\d\.]+)\s*LUFS");
            if (iMatches.Count > 0) double.TryParse(iMatches[^1].Groups[1].Value, NumberStyles.Any, inv, out lufs);

            var tpMatches = Regex.Matches(stderr, @"Peak:\s*([-\d\.]+)\s*dBFS");
            if (tpMatches.Count > 0) double.TryParse(tpMatches[^1].Groups[1].Value, NumberStyles.Any, inv, out peak);

            var lraMatches = Regex.Matches(stderr, @"LRA:\s*([-\d\.]+)\s*LU");
            if (lraMatches.Count > 0) double.TryParse(lraMatches[^1].Groups[1].Value, NumberStyles.Any, inv, out lra);

            return new AudioQcReport(lufs, peak, lra, sr, iMatches.Count > 0);
        }
        catch
        {
            return new AudioQcReport(0, 0, 0, 44100, false);
        }
    }

    private void UpdateCurrentQcDashboard()
    {
        if (_currentItem == null || TxtLufs == null)
        {
            ResetQcDisplay();
            return;
        }

        bool isAfter = BtnAfter.IsChecked == true && !string.IsNullOrEmpty(_currentItem.OutputPath);
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

        if (BtnAfter.IsChecked == true && string.IsNullOrEmpty(_currentItem.OutputPath))
        {
            BtnBefore.IsChecked = true;
            StatusText.Text = "Press Start Mastering below to generate After preview";
            return;
        }

        TimeSpan pos = InactivePlayer.Position;
        InactivePlayer.Pause();

        if (ActivePlayer.NaturalDuration.HasTimeSpan && pos > ActivePlayer.NaturalDuration.TimeSpan)
            pos = ActivePlayer.NaturalDuration.TimeSpan;

        ActivePlayer.Position = pos;
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

    private static (float[] peaks, float trackMaxPeak, float trackAvgRms) ExtractWaveformData(string ffmpegPath, string filePath, int bars)
    {
        var result = new float[bars];
        float globalMaxPeak = 0f;
        double globalSumSq = 0;
        long totalFrames = 0;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-v error -i \"{filePath}\" -vn -ac 2 -ar 22050 -f s16le -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (result, 0f, 0f);

            using var ms = new MemoryStream();
            proc.StandardOutput.BaseStream.CopyTo(ms);
            proc.WaitForExit();

            byte[] bytes = ms.ToArray();
            int stereoFrames = bytes.Length / 4; // 2 channels * 2 bytes
            if (stereoFrames == 0) return (result, 0f, 0f);

            int step = Math.Max(1, stereoFrames / bars);
            for (int b = 0; b < bars; b++)
            {
                int start = b * step;
                float maxPeak = 0f;
                double sumSq = 0;
                int count = 0;

                for (int i = 0; i < step && (start + i) < stereoFrames; i++)
                {
                    int offset = (start + i) * 4;
                    float left = BitConverter.ToInt16(bytes, offset) / 32768f;
                    float right = BitConverter.ToInt16(bytes, offset + 2) / 32768f;

                    float absL = Math.Abs(left);
                    float absR = Math.Abs(right);
                    float framePeak = Math.Max(absL, absR);
                    float sideEnergy = Math.Abs(left - right) * 0.35f;
                    float effectiveSample = Math.Min(1.0f, framePeak + sideEnergy * 0.25f);

                    if (effectiveSample > maxPeak) maxPeak = effectiveSample;
                    if (framePeak > globalMaxPeak) globalMaxPeak = framePeak;

                    double frameSq = (left * left + right * right) * 0.5;
                    sumSq += frameSq;
                    globalSumSq += frameSq;
                    count++;
                    totalFrames++;
                }

                float rms = count > 0 ? (float)Math.Sqrt(sumSq / count) : 0f;
                float combined = 0.58f * maxPeak + 0.42f * Math.Min(1.0f, rms * 1.85f);
                result[b] = Math.Clamp(combined, 0.04f, 1.0f);
            }
        }
        catch { }

        float avgRms = totalFrames > 0 ? (float)Math.Sqrt(globalSumSq / totalFrames) : 0f;
        return (result, globalMaxPeak, avgRms);
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
        double h = WaveformCanvas.ActualHeight > 0 ? WaveformCanvas.ActualHeight : 64;
        double centerY = h / 2.0;

        // Center reference axis line
        var centerLine = new Line
        {
            X1 = 0,
            Y1 = centerY,
            X2 = w,
            Y2 = centerY,
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1D2230")),
            StrokeThickness = 1
        };
        WaveformCanvas.Children.Add(centerLine);

        int count = raw.Length;
        double barSlot = w / count;
        double barWidth = Math.Max(1.8, barSlot - 1.2);
        double maxDrawH = Math.Max(12, h - 6);

        bool hasMaster = master != null && master.Length == count;
        bool isAfterSelected = BtnAfter.IsChecked == true && hasMaster;

        if (!hasMaster)
        {
            // Only Raw loaded (before pressing Start Mastering)
            float rawMax = Math.Max(0.15f, raw.Max());
            var rawBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F472B6"));

            for (int i = 0; i < count; i++)
            {
                double norm = Math.Clamp(raw[i] / rawMax, 0.05, 1.0);
                double barH = Math.Max(3.0, norm * maxDrawH);
                var rect = new Rectangle
                {
                    Width = barWidth,
                    Height = barH,
                    RadiusX = 1.2,
                    RadiusY = 1.2,
                    Fill = rawBrush,
                    Opacity = 0.78
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

        // Overlapped Before / After Comparison Waveform with Highlighted Delta
        float[] masterArr = master!;
        float sharedScale = Math.Max(0.15f, Math.Max(raw.Max(), masterArr.Max()));

        var baseBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isAfterSelected ? "#323A52" : "#3A3248"));
        var boostBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4ADE80")); // Vivid Neon Green (+After > Before)
        var cutBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F472B6"));   // Vivid Rose Pink (-Before > After)

        int boostedBars = 0;
        int cutBars = 0;

        for (int i = 0; i < count; i++)
        {
            double rNorm = Math.Clamp(raw[i] / sharedScale, 0.04, 1.0);
            double mNorm = Math.Clamp(masterArr[i] / sharedScale, 0.04, 1.0);

            double rawH = Math.Max(3.0, rNorm * maxDrawH);
            double masterH = Math.Max(3.0, mNorm * maxDrawH);

            double baseH = Math.Min(rawH, masterH);
            double rawDelta = masterArr[i] - raw[i];
            double absDelta = Math.Abs(rawDelta);

            // Amplify non-zero differences so subtle mastering/limiter/EQ changes pop clearly
            double visualDiffH = 0;
            if (absDelta >= 0.0025)
            {
                double magnified = (absDelta / sharedScale) * maxDrawH * 2.8;
                visualDiffH = Math.Clamp(Math.Max(4.0, magnified), 4.0, maxDrawH - 3.0);
                if (baseH + visualDiffH > maxDrawH)
                    baseH = Math.Max(3.0, maxDrawH - visualDiffH);
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
                    RadiusX = 1.2,
                    RadiusY = 1.2,
                    Fill = isBoost ? boostBrush : cutBrush,
                    Opacity = (isBoost == isAfterSelected) ? 1.0 : 0.88
                };
                Canvas.SetLeft(diffRect, x);
                Canvas.SetTop(diffRect, centerY - outerH / 2.0);
                WaveformCanvas.Children.Add(diffRect);
            }

            // Shared base body drawn on top in the middle so outer tips glow in Green/Pink
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

public readonly record struct AudioQcReport(double Lufs, double Peak, double Lra, int SampleRate, bool Valid);

public class AudioTaskItem : INotifyPropertyChanged
{
    public string FilePath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string FileName { get; set; } = "";
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
