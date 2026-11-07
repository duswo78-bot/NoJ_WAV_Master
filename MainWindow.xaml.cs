using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
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
    private bool _suppressAutoManual;
    private string? _ffmpegPath;

    private MediaPlayer ActivePlayer => BtnAfter.IsChecked == true ? _masterPlayer : _rawPlayer;
    private MediaPlayer InactivePlayer => BtnAfter.IsChecked == true ? _rawPlayer : _masterPlayer;

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
    }

    private void FxControl_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressAutoManual) return;
        if (ModeManual.IsChecked != true) ModeManual.IsChecked = true;
    }

    private void FxSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _suppressAutoManual) return;
        if (ModeManual.IsChecked != true) ModeManual.IsChecked = true;
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
            await ProcessFilesAsync(dropped);
        }
    }

    private async void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        if (Tasks.Count > 0) return;
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Audio/Video Files|*.wav;*.mp3;*.flac;*.aiff;*.aac;*.m4a;*.ogg;*.mp4|All Files|*.*"
        };
        if (dlg.ShowDialog() == true)
            await ProcessFilesAsync(dlg.FileNames);
    }

    private async void Reprocess_Click(object sender, RoutedEventArgs e)
    {
        if (Tasks.Count == 0) return;
        string? prevSelectedFile = (FileList.SelectedItem as AudioTaskItem)?.FilePath;
        ReleasePlayers();
        _currentItem = null;
        string[] files = Tasks.Select(t => t.FilePath).ToArray();
        Tasks.Clear();
        await ProcessFilesAsync(files, prevSelectedFile);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        ReleasePlayers();
        _currentItem = null;
        WaveformCanvas.Children.Clear();
        NowPlayingText.Text = "Select a processed track to preview";
        TimeText.Text = "00:00 / 00:00";
        Tasks.Clear();
        FileList.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        ClearBtn.Visibility = Visibility.Collapsed;
        ReprocessBtn.Visibility = Visibility.Collapsed;
        StatusText.Text = "Ready · Metadata & watermark clean active";
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

    private async Task ProcessFilesAsync(string[] rawPaths, string? preferredSelectionPath = null)
    {
        List<string> files = ExpandInputFiles(rawPaths);
        if (files.Count == 0)
        {
            if (Tasks.Count == 0)
            {
                EmptyState.Visibility = Visibility.Visible;
                FileList.Visibility = Visibility.Collapsed;
            }
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        FileList.Visibility = Visibility.Visible;
        ClearBtn.Visibility = Visibility.Visible;
        ReprocessBtn.Visibility = Visibility.Visible;

        string ffmpeg;
        try
        {
            ffmpeg = await EnsureFFmpegAsync();
        }
        catch
        {
            StatusText.Text = "FFmpeg setup failed. Check internet or place ffmpeg.exe next to app.";
            return;
        }

        string filterArg = BuildFilterArgument();
        var (ext, codecArg, suffix) = GetFormatArguments();

        var newItems = new List<AudioTaskItem>();
        foreach (var f in files)
        {
            var item = new AudioTaskItem
            {
                FilePath = f,
                FileName = System.IO.Path.GetFileName(f),
                Status = "Queued",
                StatusColor = "#8E93A6"
            };
            Tasks.Add(item);
            newItems.Add(item);
        }

        StatusText.Text = $"Processing {newItems.Count} file(s)...";
        int successCount = 0;

        foreach (var item in newItems)
        {
            string expectedOut = BuildOutputPath(item.FilePath, suffix, ext);
            if (_currentItem != null &&
                (string.Equals(_currentItem.OutputPath, expectedOut, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(_currentItem.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)))
            {
                ReleasePlayers();
                _currentItem = null;
            }

            item.Status = "Working...";
            item.StatusColor = "#7C85F2";

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
                item.Status = "Failed";
                item.StatusColor = "#F87171";
            }
        }

        StatusText.Text = $"Completed {successCount}/{newItems.Count} file(s). Click a track to preview.";

        AudioTaskItem? targetSelect = null;
        if (!string.IsNullOrEmpty(preferredSelectionPath))
        {
            targetSelect = newItems.FirstOrDefault(i =>
                !string.IsNullOrEmpty(i.OutputPath) &&
                string.Equals(i.FilePath, preferredSelectionPath, StringComparison.OrdinalIgnoreCase));
        }
        targetSelect ??= newItems.FirstOrDefault(i => !string.IsNullOrEmpty(i.OutputPath));

        if (FileList.SelectedItem == null && targetSelect != null)
            FileList.SelectedItem = targetSelect;
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
        if (FileList.SelectedItem is not AudioTaskItem item || string.IsNullOrEmpty(item.OutputPath)) return;
        ReleasePlayers();
        _currentItem = item;
        NowPlayingText.Text = item.FileName;
        TimeText.Text = "00:00 / 00:00";
        Playhead.Margin = new Thickness(0);

        if (File.Exists(item.FilePath))
            _rawPlayer.Open(new Uri(item.FilePath));
        if (File.Exists(item.OutputPath))
            _masterPlayer.Open(new Uri(item.OutputPath));

        await LoadWaveformsAsync(item);
    }

    private async Task LoadWaveformsAsync(AudioTaskItem item)
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

        item.RawPeaks ??= await Task.Run(() => ExtractWaveformPeaks(ffmpeg, item.FilePath, 110));
        item.MasterPeaks ??= await Task.Run(() => ExtractWaveformPeaks(ffmpeg, item.OutputPath, 110));

        if (_currentItem == item)
            DrawCurrentWaveform();
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

        TimeSpan pos = InactivePlayer.Position;
        InactivePlayer.Pause();

        if (ActivePlayer.NaturalDuration.HasTimeSpan && pos > ActivePlayer.NaturalDuration.TimeSpan)
            pos = ActivePlayer.NaturalDuration.TimeSpan;

        ActivePlayer.Position = pos;
        if (_isPlaying)
            ActivePlayer.Play();

        DrawCurrentWaveform();
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

    private static float[] ExtractWaveformPeaks(string ffmpegPath, string filePath, int bars)
    {
        var result = new float[bars];
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-v error -i \"{filePath}\" -vn -ac 1 -ar 4000 -f s16le -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return result;

            using var ms = new MemoryStream();
            proc.StandardOutput.BaseStream.CopyTo(ms);
            proc.WaitForExit();

            byte[] bytes = ms.ToArray();
            int sampleCount = bytes.Length / 2;
            if (sampleCount == 0) return result;

            int step = Math.Max(1, sampleCount / bars);
            for (int b = 0; b < bars; b++)
            {
                int start = b * step;
                float max = 0f;
                for (int i = 0; i < step && (start + i) < sampleCount; i++)
                {
                    short val = BitConverter.ToInt16(bytes, (start + i) * 2);
                    float norm = Math.Abs(val / 32768f);
                    if (norm > max) max = norm;
                }
                result[b] = Math.Clamp(max, 0.06f, 1.0f);
            }
        }
        catch { }
        return result;
    }

    private void DrawCurrentWaveform()
    {
        if (_currentItem == null) return;
        bool isAfter = BtnAfter.IsChecked == true;
        float[]? peaks = isAfter ? _currentItem.MasterPeaks : _currentItem.RawPeaks;
        if (peaks == null || peaks.Length == 0) return;
        DrawWaveform(peaks, isAfter);
    }

    private void DrawWaveform(float[] peaks, bool isAfter)
    {
        WaveformCanvas.Children.Clear();
        double w = WaveformCanvas.ActualWidth > 0 ? WaveformCanvas.ActualWidth : 488;
        double h = WaveformCanvas.ActualHeight > 0 ? WaveformCanvas.ActualHeight : 54;

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isAfter ? "#4ADE80" : "#F472B6"));
        double barSlot = w / peaks.Length;
        double barWidth = Math.Max(2.0, barSlot - 2.0);

        for (int i = 0; i < peaks.Length; i++)
        {
            double barHeight = Math.Max(4, peaks[i] * (h - 8));
            var rect = new Rectangle
            {
                Width = barWidth,
                Height = barHeight,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = brush,
                Opacity = 0.85
            };
            Canvas.SetLeft(rect, i * barSlot + 1);
            Canvas.SetTop(rect, (h - barHeight) / 2);
            WaveformCanvas.Children.Add(rect);
        }
    }
}

public class AudioTaskItem : INotifyPropertyChanged
{
    public string FilePath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public float[]? RawPeaks { get; set; }
    public float[]? MasterPeaks { get; set; }

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
