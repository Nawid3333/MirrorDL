// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace MirrorDL.App;

/// <summary>The links of one check: a <see cref="MirrorDL.Batch"/>, and what the window needs of it.</summary>
sealed class Run(Batch batch)
{
    public Batch Batch { get; } = batch;
    public CancellationTokenSource? Cts { get; set; }
    public Task<int>? Task { get; set; }
    public bool IsRunning => Task is { IsCompleted: false };
    /// <summary>The batch's own messages: checking the links, what was found on pages, the summary.</summary>
    public ObservableCollection<LogLine> Log { get; } = [];
    public List<DownloadRow> Rows { get; } = [];
    public DownloadRow? RowOf(BatchFile file) => Rows.FirstOrDefault(r => r.File == file);
}

/// <summary>The download list: several batches at once, each downloading its files one after another.</summary>
public sealed partial class MainWindow
{
    public ObservableCollection<DownloadRow> Downloads { get; } = [];
    public ObservableCollection<LinkRow> Links { get; } = [];
    public ObservableCollection<LogLine> LogLines { get; } = [];

    readonly List<Run> _runs = [];
    DownloadRow? _selected;
    IReadOnlyList<Mirror>? _shownLinks; // the links the table shows
    bool _awake;

    /// <summary>A run of a new batch, its messages and events going to the window.</summary>
    Run NewRun(Batch batch)
    {
        var run = new Run(batch);
        batch.ConfirmAsync = (file, question) => ConfirmAsync(run, file, question);
        batch.Log += (file, text, kind) => DispatcherQueue.TryEnqueue(() =>
        {
            var line = new LogLine(text, Brushes.For(kind));
            var row = file == null ? null : run.RowOf(file);
            (row?.Log ?? run.Log).Add(line);
            if (_review == run && ReviewRing.IsActive) ReviewText.Text = text;
            if (_selected is { } shown && (shown == row || row == null && shown.Run == run))
            {
                LogLines.Add(line);
                LogList.ScrollIntoView(line);
            }
        });
        batch.FileStarted += file => DispatcherQueue.TryEnqueue(() =>
        {
            if (run.RowOf(file) is not { } row) return;
            row.Status = null;
            row.Update();
            UpdateList();
            // The list follows the download, unless the user looks at another one that isn't done.
            if (_selected == null || _selected.Run == run || _selected.File.IsFinished) Select(row);
        });
        batch.LinksChecked += file => DispatcherQueue.TryEnqueue(() =>
        {
            if (_selected?.File == file) _shownLinks = null; // the table is made anew with what is known now
        });
        batch.FileEnded += file => DispatcherQueue.TryEnqueue(() =>
        {
            if (run.RowOf(file) is not { } row) return;
            if (file.Session is { } session) row.Status = session.GetStatus();
            row.Update();
            UpdateList();
            if (file.IsFinished) AddHistory(file);
            SaveList();
        });
        return run;
    }

    void AddRun(Run run)
    {
        _runs.Add(run);
        foreach (var file in run.Batch.Files.Where(f => f.State != FileState.Removed))
        {
            var row = new DownloadRow(run, file);
            row.Update();
            run.Rows.Add(row);
            Downloads.Add(row);
        }
        SaveList();
        UpdateList();
    }

    /// <summary>Downloads the batch's files that aren't done, one after another, next to any other batch running.</summary>
    async void StartRun(Run run)
    {
        if (run.IsRunning || _closing) return;
        // The settings as they are now, also for a download checked or saved before they changed.
        foreach (var file in run.Batch.Files)
        {
            file.Job.DeleteLeftovers = _settings.DeleteLeftovers;
            file.Job.Connections = _settings.Connections;
            file.Job.PerServer = _settings.PerLink;
        }
        var cts = run.Cts = new CancellationTokenSource();
        run.Task = Task.Run(() => run.Batch.RunAsync(cts.Token));
        foreach (var row in run.Rows) row.Update();
        UpdateList();
        try
        {
            await run.Task;
        }
        catch (Exception ex)
        {
            run.Log.Add(new LogLine("Unexpected error: " + ex, Brushes.Critical));
        }
        if (run.Cts == cts) run.Cts = null; // not a newer start's, after a quick Resume
        if (_shutDown) return;
        foreach (var row in run.Rows) row.Update();
        SaveList();
        UpdateList();
        if (_runs.Any(r => r.IsRunning)) return;
        if (_closing) Close();
        else
        {
            if (_settings.FlashWhenDone) WindowsShell.Flash(_hwnd);
            // A run that ended by itself (a Pause would have cancelled it): the "When done" choice counts.
            if (!cts.IsCancellationRequested) StartWhenDone();
        }
    }

    void RowPause_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadRow row) Pause(row.Run);
    }

    void Pause(Run run)
    {
        run.Cts?.Cancel();
        if (_questionRun == run) _question?.Hide(); // an open question counts as "no", so the download can stop
    }

    void RowResume_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadRow row) StartRun(row.Run);
    }

    void RowFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadRow row) ShowInExplorer(row.File.SavedPath);
    }

    /// <summary>Takes a download off the list - not while it downloads. Its files on the disk stay as they are.</summary>
    void RowRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadRow row) RemoveRow(row);
        SaveList();
        UpdateList();
    }

    void RemoveRow(DownloadRow row)
    {
        if (!row.Run.Batch.Remove(row.File)) return;
        row.Run.Rows.Remove(row);
        Downloads.Remove(row);
        if (_selected == row) Select(null);
        if (row.Run.Rows.Count == 0 && !row.Run.IsRunning)
        {
            _runs.Remove(row.Run);
            row.Run.Batch.Release();
        }
    }

    void ResumeAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var run in _runs.Where(r => !r.IsRunning && r.Rows.Any(row => !row.File.IsFinished && !row.File.IsPage)).ToList())
            StartRun(run);
    }

    void PauseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var run in _runs.Where(r => r.IsRunning)) Pause(run);
    }

    void ClearFinished_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Downloads.Where(r => r.File.IsFinished).ToList()) RemoveRow(row);
        SaveList();
        UpdateList();
    }

    void DownloadList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DownloadList.SelectedItem is DownloadRow row && row != _selected) Select(row);
    }

    void Select(DownloadRow? row)
    {
        _selected = row;
        if (!ReferenceEquals(DownloadList.SelectedItem, row)) DownloadList.SelectedItem = row;
        _shownLinks = null;
        LogLines.Clear();
        if (row != null)
        {
            foreach (var line in row.Run.Log.Concat(row.Log)) LogLines.Add(line);
            if (LogLines.Count > 0) LogList.ScrollIntoView(LogLines[^1]);
        }
        ShowDetails();
    }

    /// <summary>Every half second: the numbers of what downloads, the list, the selected download, the taskbar.</summary>
    void Refresh()
    {
        if (_shutDown) return;
        foreach (var run in _runs.Where(r => r.IsRunning))
            if (run.Batch.Current is { Session: { } session } file && run.RowOf(file) is { } row)
                row.Status = session.GetStatus(); // the one place that reads it while it runs: it also measures the speed
        foreach (var row in Downloads) row.Update();
        ShowDetails();
        UpdateList();
    }

    /// <summary>The line above the list, the buttons, the window title, the taskbar button, keeping the PC awake.</summary>
    void UpdateList()
    {
        NoDownloadsText.Visibility = Downloads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadList.Visibility = Downloads.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        int running = Downloads.Count(r => r.File.State == FileState.Running);
        int waiting = Downloads.Count(r => r.File.State is FileState.Waiting or FileState.Paused);
        int done = Downloads.Count(r => r.File.IsFinished);
        int failed = Downloads.Count(r => r.File.State == FileState.Failed);
        double speed = Downloads.Where(r => r.File.State == FileState.Running).Sum(r => r.Status?.Speed ?? 0);
        var parts = new List<string>();
        if (running > 0) parts.Add($"{running} downloading at {Format.Speed(speed)}");
        if (waiting > 0) parts.Add($"{waiting} waiting or paused");
        if (done > 0) parts.Add($"{done} done");
        if (failed > 0) parts.Add($"{failed} failed");
        SummaryText.Text = string.Join(" · ", parts);
        PauseAllButton.IsEnabled = _runs.Any(r => r.IsRunning);
        ResumeAllButton.IsEnabled = _runs.Any(r => !r.IsRunning && r.Rows.Any(row => !row.File.IsFinished && !row.File.IsPage));
        ClearFinishedButton.IsEnabled = done > 0;

        // All files of the batches that run: done ones in full, the others as far as they got.
        var active = Downloads.Where(r => r.Run.IsRunning && r.File.Size is > 0).ToList();
        long total = active.Sum(r => r.File.Size!.Value);
        long have = active.Sum(r => r.File.IsFinished ? r.File.Size!.Value : r.Status?.Have ?? 0);
        bool anyRunning = _runs.Any(r => r.IsRunning);
        double percent = total > 0 ? have * 100.0 / total : 0;
        Title = anyRunning && total > 0 ? $"{percent:0}% · MirrorDL" : "MirrorDL";
        if (_settings.TaskbarProgress && anyRunning)
            WindowsShell.SetProgress(_hwnd, total > 0 ? WindowsShell.Progress.Normal : WindowsShell.Progress.Indeterminate, percent);
        else
            WindowsShell.SetProgress(_hwnd, WindowsShell.Progress.None);
        bool awake = _settings.KeepAwake && anyRunning;
        if (awake != _awake) WindowsShell.KeepAwake(_awake = awake);
    }

    /// <summary>The selected download: its numbers, its links, how it ended.</summary>
    void ShowDetails()
    {
        if (_selected is not { } row)
        {
            DetailsCard.Visibility = Visibility.Collapsed;
            return;
        }
        DetailsCard.Visibility = Visibility.Visible;
        var file = row.File;
        var s = row.Status;
        bool running = file.State == FileState.Running;
        bool verifying = running && s?.Phase == Phase.Verifying;
        double percent = file.IsFinished ? 100 : verifying ? s!.VerifyPercent : s?.Percent ?? 0;

        FileText.Text = file.Name;
        PhaseText.Text = running && row.Run.Cts?.IsCancellationRequested == true ? "Pausing and saving the progress…" : row.StatusText;
        Progress.IsIndeterminate = row.Indeterminate;
        Progress.Value = percent;
        Progress.ShowPaused = row.ShowPaused;
        Progress.ShowError = row.ShowError;
        PercentText.Text = row.PercentLabel;
        long? size = file.Size ?? s?.Size;
        DownloadedText.Text = s == null ? "–" : size is long all ? $"{Format.Bytes(s.Have)} of {Format.Bytes(all)}" : Format.Bytes(s.Have);
        RemainingText.Text = s != null && size is long whole ? Format.Bytes(whole - s.Have) : "–";
        SpeedText.Text = running && s?.Phase == Phase.Downloading ? Format.Speed(s.Speed) : "–";
        AverageText.Text = s?.AverageSpeed > 0 ? Format.Speed(s.AverageSpeed) : "–";
        ElapsedText.Text = s?.Elapsed > TimeSpan.Zero ? Format.Duration(s.Elapsed.TotalSeconds) : "–";
        LeftText.Text = running && s is { Phase: Phase.Downloading, SecondsLeft: double left } ? Format.Duration(left) : "–";
        ConnectionsText.Text = running && s != null ? s.Connections.ToString() : "–";
        LinksText.Text = $"{file.Links.Count(m => m.Usable)} of {file.Links.Count}";

        if (!ReferenceEquals(_shownLinks, file.Links))
        {
            _shownLinks = file.Links;
            Links.Clear();
            foreach (var m in file.Links) Links.Add(new LinkRow(m));
        }
        for (int i = 0; i < Links.Count; i++)
            Links[i].Update(running && s != null && i < s.Links.Count ? s.Links[i] : null, running ? s?.Phase ?? Phase.Checking : null);

        ShowOutcome(row);
        DrawMap(file.IsFinished ? null : s, file.IsFinished ? file.Size : null);
    }

    void ShowOutcome(DownloadRow row)
    {
        var file = row.File;
        ShowButton.Content = file.IsPage ? "Open page" : "Show in folder";
        (InfoBarSeverity Severity, string Title, string? Message, bool Button)? outcome = file.State switch
        {
            FileState.Done when file.Session?.SizeOnly == true => (InfoBarSeverity.Warning, "Only the size could be checked",
                $"These links can't send parts of the file to compare. Paste its checksum with the links to check it: {file.SavedPath}", true),
            FileState.Done => (InfoBarSeverity.Success, "Download complete", file.SavedPath, true),
            FileState.Checked => (InfoBarSeverity.Success, "The file was there already",
                $"It was only checked, and stays as it was: {file.SavedPath}", true),
            FileState.Mismatch when file.CheckedOnly => (InfoBarSeverity.Warning, "The file doesn't match",
                $"It is damaged, or the checksum or the links are for another version. It was left as it was: {file.SavedPath}", true),
            FileState.Mismatch => (InfoBarSeverity.Warning, "Saved, but it doesn't match",
                $"The file is damaged, or the checksum is for another version: {file.SavedPath}", true),
            FileState.Paused => (InfoBarSeverity.Informational, "Paused",
                (file.Message ?? "The progress is saved.") + " Press Resume to go on, also after closing MirrorDL.", false),
            FileState.Failed when file.IsPage => (InfoBarSeverity.Warning, "A web page", file.Message, true),
            FileState.Failed => (InfoBarSeverity.Error, "Download failed", file.Message ?? "See the messages below.", false),
            _ => null,
        };
        if (outcome is not { } o)
        {
            Result.IsOpen = false;
            return;
        }
        Result.Severity = o.Severity;
        Result.Title = o.Title;
        Result.Message = o.Message ?? "";
        ShowButton.Visibility = o.Button ? Visibility.Visible : Visibility.Collapsed;
        Result.IsOpen = true;
    }

    void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selected?.File is not { } file) return;
        if (file.IsPage) OpenPage(file);
        else ShowInExplorer(file.SavedPath);
    }

    /// <summary>Draws the file from start to end: filled where downloaded, half-filled where partly.</summary>
    void DrawMap(Status? s, long? finished)
    {
        Map.Children.Clear();
        long size = finished ?? s?.Size ?? 0;
        double width = Map.ActualWidth, height = Map.ActualHeight;
        if (size <= 0 || width <= 0 || height <= 0) return;
        if (finished != null)
        {
            Map.Children.Add(new Rectangle { Width = width, Height = height, Fill = Brushes.Accent });
            return;
        }

        int cells = Math.Max(1, (int)(width / 2));
        double perCell = (double)size / cells;
        var covered = new double[cells];
        foreach (var (start, end) in Ranges.Invert(s!.Missing, size))
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

    void Map_SizeChanged(object sender, SizeChangedEventArgs e) => ShowDetails();

    /// <summary>Keeps the list for next time (if the user wants it): unfinished downloads go on after a restart.</summary>
    void SaveList()
    {
        if (!_settings.KeepList)
        {
            Storage.Delete("downloads.json");
            return;
        }
        Storage.Write("downloads.json", _runs.Where(r => r.Rows.Count > 0).Select(r => r.Batch.ToSaved()).ToList());
    }

    void RestoreList()
    {
        if (!_settings.KeepList || Storage.Read<List<SavedBatch>>("downloads.json") is not { } saved) return;
        foreach (var batch in saved)
        {
            try
            {
                AddRun(NewRun(Batch.FromSaved(batch)));
            }
            catch (Exception e) when (e is UriFormatException or ArgumentException or NullReferenceException)
            {
                // One damaged entry doesn't cost the others.
            }
        }
        if (Downloads.FirstOrDefault(r => !r.File.IsFinished) is { } first) Select(first);
    }
}
