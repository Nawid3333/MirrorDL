// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace MirrorDL.App;

/// <summary>The new download: a box per link, checksum and unfinished file; checking the links; what they lead to.</summary>
public sealed partial class MainWindow
{
    public ObservableCollection<EntryRow> LinkEntries { get; } = [];
    public ObservableCollection<EntryRow> ChecksumEntries { get; } = [];
    public ObservableCollection<EntryRow> PartEntries { get; } = [];
    public ObservableCollection<ReviewRow> ReviewRows { get; } = [];

    Run? _review;                 // the links being checked, or checked and shown
    CancellationTokenSource? _reviewCts;
    List<EntryRow> _checkedEntries = []; // the boxes the check took, emptied once their files download

    ObservableCollection<EntryRow> ListOf(EntryKind kind) => kind switch
    {
        EntryKind.Link => LinkEntries,
        EntryKind.Checksum => ChecksumEntries,
        _ => PartEntries,
    };

    EntryRow NewEntry(EntryKind kind, string text = "")
    {
        var row = new EntryRow(kind, text);
        row.TextChanged += _ => KeepEmptyBox(kind);
        return row;
    }

    /// <summary>There is always one empty box at the end of each list, to type or paste the next one into.</summary>
    void KeepEmptyBox(EntryKind kind)
    {
        var list = ListOf(kind);
        if (list.Count == 0 || list[^1].Text.Trim().Length > 0) list.Add(NewEntry(kind));
    }

    /// <summary>Adds a box with <paramref name="text"/> before the empty one at the end, unless the same is there already.</summary>
    bool AddEntry(EntryKind kind, string text)
    {
        text = text.Trim();
        var list = ListOf(kind);
        if (text.Length == 0 || list.Any(e => string.Equals(e.Text.Trim(), text, StringComparison.OrdinalIgnoreCase))) return false;
        int at = list.Count > 0 && list[^1].Text.Trim().Length == 0 ? list.Count - 1 : list.Count;
        list.Insert(at, NewEntry(kind, text));
        KeepEmptyBox(kind);
        return true;
    }

    void RemoveEntry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EntryRow row) return;
        ListOf(row.Kind).Remove(row);
        KeepEmptyBox(row.Kind);
    }

    void ClearEntries_Click(object sender, RoutedEventArgs e) => ClearEntries();

    void ClearEntries()
    {
        foreach (var kind in new[] { EntryKind.Link, EntryKind.Checksum, EntryKind.Unfinished })
        {
            ListOf(kind).Clear();
            KeepEmptyBox(kind);
        }
    }

    /// <summary>
    /// Pasting one thing puts it into the box; pasting several (a list of links, a checksum file) gives each a box
    /// of its own, in the list it belongs to - whichever box it was pasted into.
    /// </summary>
    async void Entry_Paste(object sender, TextControlPasteEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not EntryRow row) return;
        DataPackageView content;
        try
        {
            content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
        }
        catch (Exception)
        {
            return; // another program holds the clipboard: the box pastes as it would
        }
        e.Handled = true; // before the first await: the box doesn't paste it itself
        try
        {
            string text = (await content.GetTextAsync()).Trim();
            if (!text.Contains('\n') && !text.Contains('\r') && KindOf(text) is var kind && (kind == null || kind == row.Kind)
                && (row.Kind != EntryKind.Link || !text.Contains(' ')))
            {
                box.SelectedText = text;
                box.SelectionStart += box.SelectionLength;
                box.SelectionLength = 0;
                return;
            }
            Distribute(text);
        }
        catch (Exception ex)
        {
            ShowNotice(InfoBarSeverity.Warning, "Couldn't paste", ex.Message);
        }
    }

    /// <summary>What a piece of text is: a link, a checksum (also with its file's name), a file; null for anything else.</summary>
    static EntryKind? KindOf(string text)
    {
        text = text.Trim().Trim('"');
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return EntryKind.Link;
        if (Input.NamedChecksum(text) != null || Checksum.TryParse(text, out _)) return EntryKind.Checksum;
        try
        {
            if (Path.IsPathRooted(text) && File.Exists(text)) return EntryKind.Unfinished;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { }
        return null;
    }

    /// <summary>
    /// Sorts pasted or dropped text into the boxes: links, checksums (a checksum on the line of a link stays with it),
    /// unfinished files, a folder to save in; a .txt file's lines as if pasted. Says what went where.
    /// </summary>
    void Distribute(string text)
    {
        int links = 0, checksums = 0, files = 0;
        var ignored = new List<string>();
        var lists = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // .txt files read already
        void Use(string raw)
        {
            foreach (string full in raw.Split('\r', '\n'))
            {
                string line = full.Trim().Trim('"');
                if (line.Length == 0) continue;
                if (Input.NamedChecksum(line) != null)
                {
                    if (AddEntry(EntryKind.Checksum, line)) checksums++;
                    continue;
                }
                if (IsFolder(line))
                {
                    SaveToBox.Text = line;
                    continue;
                }
                if (IsFile(line))
                {
                    if (line.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    {
                        if (lists.Add(Path.GetFullPath(line)) && lists.Count <= 100) Use(File.ReadAllText(line));
                    }
                    else if (AddEntry(EntryKind.Unfinished, Path.GetFullPath(line))) files++;
                    continue;
                }
                Uri? link = null;
                var sums = new List<string>();
                foreach (string token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = token.Trim('"', '\'', '<', '>', '(', ')', ',', ';');
                    if (KindOf(t) == EntryKind.Link)
                    {
                        if (AddEntry(EntryKind.Link, t)) links++;
                        link ??= new Uri(t);
                    }
                    else if (Checksum.TryParse(t, out string? hex)) sums.Add(hex);
                    else ignored.Add(t);
                }
                foreach (string hex in sums)
                    if (AddEntry(EntryKind.Checksum, link != null ? $"{hex}  {link}" : hex)) checksums++;
            }
        }
        Use(text);
        var added = new List<string>();
        if (links > 0) added.Add(Format.Plural(links, "link"));
        if (checksums > 0) added.Add(Format.Plural(checksums, "checksum"));
        if (files > 0) added.Add(Format.Plural(files, "unfinished file"));
        if (added.Count > 0 || ignored.Count > 0)
            ShowNotice(InfoBarSeverity.Informational, added.Count > 0 ? "Added " + string.Join(", ", added) : "Nothing to add",
                ignored.Count > 0 ? "Not a link, checksum or file, so left out: " + string.Join(", ", ignored.Take(5).Select(s => Format.Clip(s, 30))) +
                                    (ignored.Count > 5 ? $" and {ignored.Count - 5} more" : "") : null);
    }

    static bool IsFolder(string text)
    {
        try { return Path.IsPathRooted(text) && Directory.Exists(text); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return false; }
    }

    static bool IsFile(string text)
    {
        try { return Path.IsPathRooted(text) && File.Exists(text); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return false; }
    }

    async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            if (await picker.PickSingleFolderAsync() is { } folder) SaveToBox.Text = folder.Path;
        }
        catch (Exception ex)
        {
            ShowNotice(InfoBarSeverity.Warning, "Couldn't open the folder picker", ex.Message + " Type or paste the folder instead.");
        }
    }

    async void BrowsePart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            foreach (string type in new[] { ".part", ".aria2", ".mirrordl", "*" }) picker.FileTypeFilter.Add(type);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            foreach (var file in await picker.PickMultipleFilesAsync()) AddEntry(EntryKind.Unfinished, file.Path);
        }
        catch (Exception ex)
        {
            ShowNotice(InfoBarSeverity.Warning, "Couldn't open the file picker", ex.Message + " Paste the path or drop the file instead.");
        }
    }

    void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.WebLink)
            || e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to the new download";
        }
    }

    async void Root_Drop(object sender, DragEventArgs e)
    {
        // async void: an exception here would end the app, and a drop can fail for reasons outside it (the
        // source app took the data back, a file in the cloud could not be fetched).
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
                Distribute(string.Join("\n", (await e.DataView.GetStorageItemsAsync()).Select(i => i.Path)));
            else if (e.DataView.Contains(StandardDataFormats.WebLink))
                Distribute((await e.DataView.GetWebLinkAsync()).ToString());
            else if (e.DataView.Contains(StandardDataFormats.Text))
                Distribute(await e.DataView.GetTextAsync());
            ShowDownloads();
        }
        catch (Exception ex)
        {
            ShowNotice(InfoBarSeverity.Warning, "Couldn't use what was dropped", ex.Message);
        }
    }

    async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync(start: !_settings.Review);

    /// <summary>The new download as the boxes say, with the settings; null (and the reason said) if it can't be.</summary>
    Job? NewJob()
    {
        var job = new Job
        {
            DeleteLeftovers = _settings.DeleteLeftovers,
            Connections = _settings.Connections,
            PerServer = _settings.PerLink,
            OneFile = !_settings.SortLinks,
            LookIntoPages = _settings.LookIntoPages,
        };
        var ignored = new List<string>();
        _checkedEntries = [.. LinkEntries.Concat(ChecksumEntries).Concat(PartEntries).Where(e => e.Text.Trim().Length > 0)];
        foreach (var entry in LinkEntries.Concat(ChecksumEntries)) Input.Add(entry.Text, job, ignored, allowFiles: false);
        foreach (var entry in PartEntries)
        {
            string path = entry.Text.Trim().Trim('"');
            if (path.Length == 0) continue;
            if (!IsFile(path))
            {
                ShowNotice(InfoBarSeverity.Warning, "Can't continue that file", $"{path} doesn't exist.");
                return null;
            }
            job.PartFiles.Add(Path.GetFullPath(path));
        }
        string saveTo = SaveToBox.Text.Trim().Trim('"');
        if (saveTo.Length > 0)
        {
            try { job.OutDir = Path.GetFullPath(saveTo); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                ShowNotice(InfoBarSeverity.Warning, "Can't use that folder", $"{saveTo}: {ex.Message}");
                return null;
            }
        }
        if (job.Urls.Count == 0)
        {
            ShowNotice(InfoBarSeverity.Warning, "No links", "Paste at least one download link, starting with http:// or https://.");
            return null;
        }
        // A link being downloaded already would only end up as a second copy - or find its file in use.
        var busy = job.Urls.Where(u => _runs.Any(r => r.Batch.Files.Any(f => !f.IsFinished && f.State != FileState.Removed && f.Job.Urls.Contains(u))
                                                      || r.Batch.Job.Urls.Contains(u) && r.Batch.Files.Any(f => !f.IsFinished && f.State != FileState.Removed)))
            .ToList();
        if (busy.Count == job.Urls.Count)
        {
            ShowNotice(InfoBarSeverity.Informational, "In the download list already", "These links are in the list below: resume them there.");
            return null;
        }
        foreach (var url in busy) job.Urls.Remove(url);
        if (busy.Count > 0)
            ShowNotice(InfoBarSeverity.Informational, "Some links are in the download list already",
                $"{Format.Plural(busy.Count, "link")} left out, since the list below has them: " + string.Join(", ", busy.Take(3).Select(u => Format.Clip(u.ToString(), 50))));
        return job;
    }

    /// <summary>
    /// Checks the links of the boxes and shows what they lead to: files, their sizes and links, pages to open. With
    /// <paramref name="start"/>, what can be downloaded starts right away.
    /// </summary>
    async Task CheckAsync(bool start)
    {
        if (NewJob() is not { } job) return;
        // Links whose files the user unticked in the review this one replaces stay unticked.
        var unticked = ReviewRows.Where(r => !r.IsSelected).SelectMany(r => r.File.Job.Urls).ToHashSet();
        CancelReview();
        var run = NewRun(new Batch(job));
        _review = run;
        var cts = _reviewCts = new CancellationTokenSource();
        ReviewRows.Clear();
        ReviewCard.Visibility = Visibility.Visible;
        ReviewRing.IsActive = true;
        ReviewRing.Visibility = Visibility.Visible;
        ReviewTitle.Text = $"Checking {Format.Plural(job.Urls.Count, "link")}…";
        ReviewText.Text = "";
        DownloadButton.IsEnabled = false;
        CancelReviewButton.Content = "Cancel";

        bool sorted;
        try
        {
            sorted = await Task.Run(() => run.Batch.SortAsync(cts.Token));
        }
        catch (Exception ex)
        {
            run.Log.Add(new LogLine("Unexpected error: " + ex, Brushes.Critical));
            ShowNotice(InfoBarSeverity.Error, "Checking the links failed", ex.Message);
            sorted = false;
        }
        if (_review != run)
        {
            run.Batch.Release(); // checked anew or cancelled meanwhile: its names are free again
            return;
        }
        ReviewRing.IsActive = false;
        ReviewRing.Visibility = Visibility.Collapsed;
        if (!sorted)
        {
            CancelReview();
            return;
        }

        var files = run.Batch.Files;
        foreach (var file in files)
        {
            var row = new ReviewRow(file);
            if (file.Job.Urls.Any(unticked.Contains)) row.IsSelected = false;
            row.PropertyChanged += (_, _) => UpdateDownloadButton();
            ReviewRows.Add(row);
        }
        int pages = files.Count(f => f.IsPage), dead = files.Count(f => !f.IsPage && f.Links.All(m => !m.Usable));
        ReviewTitle.Text = files.Count == 1 ? "The links lead to 1 file" : $"The links lead to {files.Count} files";
        var notes = new List<string> { "Tick what is to be downloaded. Point at a file to see its links." };
        if (pages > 0) notes.Add($"{Format.Plural(pages, "web page")} show no link to a file: open {(pages == 1 ? "it" : "them")}, solve the captcha if there is one, and click the download button - MirrorDL takes the link from there.");
        if (dead > 0) notes.Add($"{Format.Plural(dead, "file")} can't be downloaded: none of {(dead == 1 ? "its" : "their")} links work.");
        ReviewText.Text = string.Join(" ", notes);
        CancelReviewButton.Content = "Discard";
        UpdateDownloadButton();
        if (start && ReviewRows.Any(r => r.IsSelected)) Download_Click(this, new RoutedEventArgs());
    }

    void UpdateDownloadButton()
    {
        int n = ReviewRows.Count(r => r.IsSelected);
        DownloadButton.IsEnabled = n > 0 && !ReviewRing.IsActive;
        DownloadText.Text = n == 1 ? "Download 1 file" : $"Download {n} files";
    }

    void CancelReview_Click(object sender, RoutedEventArgs e) => CancelReview();

    void CancelReview()
    {
        ReviewRing.IsActive = false;
        _reviewCts?.Cancel();
        _reviewCts = null;
        _review?.Batch.Release();
        _review = null;
        ReviewRows.Clear();
        ReviewCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>The ticked files go into the download list and start; the boxes are emptied for the next download.</summary>
    void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_review is not { } run || ReviewRing.IsActive || !ReviewRows.Any(r => r.IsSelected)) return;
        foreach (var row in ReviewRows.Where(r => !r.IsSelected)) run.Batch.Remove(row.File);
        _review = null;
        _reviewCts = null;
        ReviewRows.Clear();
        ReviewCard.Visibility = Visibility.Collapsed;
        // Only the boxes the check took: links that came in since (the clipboard, say) stay for the next check.
        foreach (var entry in _checkedEntries) ListOf(entry.Kind).Remove(entry);
        foreach (var kind in new[] { EntryKind.Link, EntryKind.Checksum, EntryKind.Unfinished }) KeepEmptyBox(kind);
        _settings.SaveTo = SaveToBox.Text.Trim();
        _settings.Save();
        Notice.IsOpen = false;
        AddRun(run);
        StartRun(run);
    }

    /// <summary>Links from elsewhere - Click'n'Load, the clipboard, the browser: into the boxes, and checked so that the user sees them.</summary>
    void TakeLinks(IReadOnlyCollection<string> links, string from, bool check)
    {
        int added = links.Count(l => AddEntry(EntryKind.Link, l));
        if (added == 0) return;
        ShowDownloads();
        ShowNotice(InfoBarSeverity.Informational, $"{Format.Plural(added, "link")} from {from}",
            check ? $"{(added == 1 ? "It is" : "They are")} checked now; nothing is downloaded before you say so."
                  : "Press Check links to see what they lead to.");
        if (check && !_shutDown)
        {
            Activate();
            WindowsShell.Flash(_hwnd);
            _ = CheckAsync(start: false);
        }
    }
}
