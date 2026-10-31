// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace MirrorDL.App;

public sealed partial class MainWindow : Window
{
    public ObservableCollection<LinkRow> Links { get; } = [];
    public ObservableCollection<LogLine> LogLines { get; } = [];

    readonly DispatcherQueueTimer _timer;
    Session? _session;
    CancellationTokenSource? _cts;
    Task<int>? _run;
    Status? _status;
    bool _closeWhenStopped;
    Uri? _updatePage;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "mirrordl.ico"));
        TitleBarIcon.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "logo-64.png")));
        PlaceWindow();
        SaveToBox.PlaceholderText = Native.DownloadsFolder();
        VersionText.Text = AppInfo.Version;
        if (!Native.IsPackaged()) _ = CheckForUpdateAsync();

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) => Refresh();
        AppWindow.Closing += AppWindow_Closing;

        // Files dropped on MirrorDL.exe, or links passed on the command line; --start starts right away.
        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        foreach (string arg in args.Where(a => a != "--start")) Use(arg);
        if (args.Contains("--start"))
            Root.Loaded += (_, _) => Start_Click(this, new RoutedEventArgs());
    }

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        var job = new Job { Connections = Number(ConnectionsBox, 16), PerServer = Number(PerLinkBox, 8) };
        var ignored = new List<string>();
        foreach (string line in LinksBox.Text.Split('\r', '\n')) Input.Add(line, job, ignored);

        string part = PartBox.Text.Trim().Trim('"');
        if (part.Length > 0)
        {
            Input.Add(part, job, []);
            if (job.PartFile == null)
            {
                ShowResult(InfoBarSeverity.Warning, "Can't continue that file",
                    $"{part} isn't an unfinished download: a .part file, or a file with an .aria2 or .mirrordl file next to it.");
                return;
            }
        }
        string saveTo = SaveToBox.Text.Trim().Trim('"');
        if (saveTo.Length > 0)
        {
            try { job.OutDir = Path.GetFullPath(saveTo); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                ShowResult(InfoBarSeverity.Warning, "Can't use that folder", $"{saveTo}: {ex.Message}");
                return;
            }
        }
        if (job.Urls.Count == 0)
        {
            ShowResult(InfoBarSeverity.Warning, "No links", "Paste at least one download link, starting with http:// or https://.");
            return;
        }
        await RunAsync(job, ignored);
    }

    async Task RunAsync(Job job, List<string> ignored)
    {
        LogLines.Clear();
        Links.Clear();
        Result.IsOpen = false;
        if (ignored.Count > 0)
            AddLog("Ignored: " + string.Join(", ", ignored.Take(5).Select(s => Format.Clip(s, 40))) +
                   (ignored.Count > 5 ? $" and {ignored.Count - 5} more" : ""), LogKind.Dim);

        var session = new Session(job) { ConfirmAsync = ConfirmAsync };
        foreach (var m in session.Mirrors) Links.Add(new LinkRow(m));
        NoLinksText.Visibility = Visibility.Collapsed;
        session.Log += (text, kind) => DispatcherQueue.TryEnqueue(() => AddLog(text, kind));
        session.PhaseChanged += _ => DispatcherQueue.TryEnqueue(Refresh);
        session.LinksChecked += () => DispatcherQueue.TryEnqueue(Refresh);
        _session = session;
        var cts = _cts = new CancellationTokenSource();
        SetRunning(true);
        _timer.Start();
        Refresh();

        int code;
        try
        {
            _run = Task.Run(() => session.RunAsync(cts.Token));
            code = await _run;
        }
        catch (Exception ex)
        {
            AddLog("Unexpected error: " + ex, LogKind.Error);
            code = -1;
        }
        _timer.Stop();
        Refresh();
        SetRunning(false);
        ShowOutcome(session, code);
        if (_closeWhenStopped) Close();
    }

    async Task CheckForUpdateAsync()
    {
        if (await Updates.FindNewerAsync() is not { } update) return;
        _updatePage = update.Page;
        UpdateBar.Message = $"MirrorDL {update.Version} is on GitHub; you have {AppInfo.Version}.";
        UpdateBar.IsOpen = true;
    }

    void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updatePage != null) Process.Start(new ProcessStartInfo(_updatePage.ToString()) { UseShellExecute = true });
    }

    void Pause_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        PauseButton.IsEnabled = false;
        PhaseText.Text = "Pausing and saving the progress…";
    }

    /// <summary>Closing during a download pauses it first, so the progress is saved.</summary>
    void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_run is not { IsCompleted: false }) return;
        args.Cancel = true;
        _closeWhenStopped = true;
        Pause_Click(this, new RoutedEventArgs());
    }

    void Refresh()
    {
        if (_session is not { } session) return;
        var s = _status = session.GetStatus();
        bool verifying = s.Phase == Phase.Verifying;
        double percent = verifying ? s.VerifyPercent : s.Percent;

        FileText.Text = session.Target?.Name ?? "Checking the links…";
        PhaseText.Text = Describe(s);
        Progress.IsIndeterminate = s.Phase is Phase.Starting or Phase.Checking or Phase.Comparing or Phase.Finishing;
        Progress.Value = percent;
        Progress.ShowPaused = s.Phase is Phase.Paused or Phase.Cancelled;
        Progress.ShowError = s.Phase == Phase.Failed;
        PercentText.Text = s.Size is > 0 || verifying ? Format.Percent(percent) : "";

        DownloadedText.Text = s.Size is long size ? $"{Format.Bytes(s.Have)} of {Format.Bytes(size)}" : Format.Bytes(s.Have);
        RemainingText.Text = s.Size is long total ? Format.Bytes(total - s.Have) : "–";
        SpeedText.Text = s.Phase == Phase.Downloading ? Format.Speed(s.Speed) : "–";
        AverageText.Text = s.AverageSpeed > 0 ? Format.Speed(s.AverageSpeed) : "–";
        ElapsedText.Text = s.Elapsed > TimeSpan.Zero ? Format.Duration(s.Elapsed.TotalSeconds) : "–";
        LeftText.Text = s.Phase == Phase.Downloading && s.SecondsLeft is double left ? Format.Duration(left) : "–";
        ConnectionsText.Text = s.Connections.ToString();
        LinksText.Text = $"{s.Links.Count(l => l.Link.Usable)} of {s.Links.Count}";
        for (int i = 0; i < Links.Count && i < s.Links.Count; i++) Links[i].Update(s.Links[i], s.Phase);
        DrawMap();
    }

    static string Describe(Status s) => s.Phase switch
    {
        Phase.Starting or Phase.Checking => "Checking the links…",
        Phase.Comparing => "Comparing the content of the links…",
        Phase.Downloading => $"Downloading from {Format.Plural(s.Links.Count(l => l.Connections > 0), "link")}",
        Phase.Finishing => "Finishing…",
        Phase.Verifying => "Checking the checksum…",
        Phase.Finished => "Done",
        Phase.Paused => "Paused. Press Continue to go on.",
        Phase.Cancelled => "Cancelled",
        Phase.Failed => "Failed. See the details below.",
        _ => "",
    };

    /// <summary>Draws the file from start to end: filled where downloaded, half-filled where partly.</summary>
    void DrawMap()
    {
        Map.Children.Clear();
        if (_status is not { Size: long size and > 0 } s) return;
        double width = Map.ActualWidth, height = Map.ActualHeight;
        if (width <= 0 || height <= 0) return;

        int cells = Math.Max(1, (int)(width / 2));
        double perCell = (double)size / cells;
        var covered = new double[cells];
        foreach (var (start, end) in Ranges.Invert(s.Missing, size))
        {
            int first = (int)(start / perCell), last = (int)Math.Min(cells - 1, (end - 1) / perCell);
            for (int c = first; c <= last; c++)
                covered[c] += (Math.Min(end, (c + 1) * perCell) - Math.Max(start, c * perCell)) / perCell;
        }

        static int Level(double share) => share >= 0.999 ? 2 : share > 0.001 ? 1 : 0;
        var brush = Brushes.Accent;
        for (int i = 0, j; i < cells; i = j)
        {
            int level = Level(covered[i]);
            for (j = i + 1; j < cells && Level(covered[j]) == level; j++) { }
            if (level == 0) continue;
            var block = new Rectangle { Width = (j - i) * width / cells, Height = height, Fill = brush, Opacity = level == 2 ? 1 : 0.45 };
            Canvas.SetLeft(block, i * width / cells);
            Map.Children.Add(block);
        }
    }

    void Map_SizeChanged(object sender, SizeChangedEventArgs e) => DrawMap();

    void ShowOutcome(Session session, int code)
    {
        StartText.Text = "Start";
        switch (code)
        {
            case 0:
                ShowResult(InfoBarSeverity.Success, "Download complete", session.SavedPath, canShow: true);
                break;
            case 2:
                ShowResult(InfoBarSeverity.Warning, "Saved, but the checksum doesn't match",
                    $"The file is probably damaged: {session.SavedPath}", canShow: true);
                break;
            case 130 when session.Phase == Phase.Paused:
                StartText.Text = "Continue";
                ShowResult(InfoBarSeverity.Informational, "Paused",
                    "The progress is saved. Press Continue to go on, also after closing MirrorDL.");
                break;
            case 130:
                ShowResult(InfoBarSeverity.Informational, "Stopped", session.Note);
                break;
            default:
                ShowResult(InfoBarSeverity.Error, "Download failed", session.ErrorMessage ?? "See the details below.");
                break;
        }
    }

    void ShowResult(InfoBarSeverity severity, string title, string? message, bool canShow = false)
    {
        Result.Severity = severity;
        Result.Title = title;
        Result.Message = message ?? "";
        ShowButton.Visibility = canShow ? Visibility.Visible : Visibility.Collapsed;
        Result.IsOpen = true;
    }

    void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_session?.SavedPath is { } path)
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        PauseButton.IsEnabled = running;
        LinksBox.IsReadOnly = running;
        foreach (var control in new Control[] { SaveToBox, PartBox, BrowseFolderButton, BrowsePartButton, ConnectionsBox, PerLinkBox })
            control.IsEnabled = !running;
    }

    void AddLog(string text, LogKind kind)
    {
        var line = new LogLine(text, Brushes.For(kind));
        LogLines.Add(line);
        LogList.ScrollIntoView(line);
    }

    Task<bool> ConfirmAsync(string question)
    {
        var answer = new TaskCompletionSource<bool>();
        DispatcherQueue.TryEnqueue(async () =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "MirrorDL",
                Content = question,
                PrimaryButtonText = "Yes",
                CloseButtonText = "No",
                DefaultButton = ContentDialogButton.Close,
            };
            answer.SetResult(await dialog.ShowAsync() == ContentDialogResult.Primary);
        });
        return answer.Task;
    }

    async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (await picker.PickSingleFolderAsync() is { } folder) SaveToBox.Text = folder.Path;
    }

    async void BrowsePart_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        foreach (string type in new[] { ".part", ".aria2", ".mirrordl", "*" }) picker.FileTypeFilter.Add(type);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (await picker.PickSingleFileAsync() is { } file) PartBox.Text = file.Path;
    }

    void Root_DragOver(object sender, DragEventArgs e)
    {
        if (StartButton.IsEnabled && (e.DataView.Contains(StandardDataFormats.StorageItems)
            || e.DataView.Contains(StandardDataFormats.WebLink) || e.DataView.Contains(StandardDataFormats.Text)))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add";
        }
    }

    async void Root_Drop(object sender, DragEventArgs e)
    {
        // async void: an exception here would end the app, and a drop can fail for reasons outside it (the
        // source app took the data back, a file in the cloud could not be fetched).
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
                foreach (var item in await e.DataView.GetStorageItemsAsync()) Use(item.Path);
            else if (e.DataView.Contains(StandardDataFormats.WebLink))
                AppendLinks((await e.DataView.GetWebLinkAsync()).ToString());
            else if (e.DataView.Contains(StandardDataFormats.Text))
                AppendLinks(await e.DataView.GetTextAsync());
        }
        catch (Exception ex)
        {
            ShowResult(InfoBarSeverity.Warning, "Couldn't use what was dropped", ex.Message);
        }
    }

    /// <summary>A folder becomes the save folder, a .txt file adds its links, another file is the one to continue.</summary>
    void Use(string text)
    {
        text = text.Trim().Trim('"');
        if (text.Length == 0) return;
        if (Directory.Exists(text))
            SaveToBox.Text = text;
        else if (File.Exists(text))
        {
            if (text.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) AppendLinks(File.ReadAllText(text));
            else PartBox.Text = text;
        }
        else
            AppendLinks(text);
    }

    void AppendLinks(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        LinksBox.Text = LinksBox.Text.Trim().Length == 0 ? text : LinksBox.Text.TrimEnd() + "\r" + text;
    }

    static int Number(NumberBox box, int fallback) =>
        double.IsNaN(box.Value) ? fallback : (int)Math.Clamp(box.Value, 1, 64);

    /// <summary>A comfortable size that still fits on the screen, centered.</summary>
    void PlaceWindow()
    {
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = (int)Math.Min(1120 * scale, area.Width * 0.95);
        int height = (int)Math.Min(980 * scale, area.Height * 0.95);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);
}
