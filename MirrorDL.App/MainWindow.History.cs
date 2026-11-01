// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace MirrorDL.App;

/// <summary>The History: finished downloads, if the user keeps one; searchable, each with its links.</summary>
public sealed partial class MainWindow
{
    public ObservableCollection<HistoryRow> HistoryRows { get; } = [];
    List<HistoryEntry> _history = [];

    void LoadHistory() => _history = _settings.KeepHistory ? Storage.Read<List<HistoryEntry>>("history.json") ?? [] : [];

    void AddHistory(BatchFile file)
    {
        if (!_settings.KeepHistory) return;
        _history.Insert(0, new HistoryEntry(file.Name, file.Size, file.SavedPath, DateTime.UtcNow, file.State,
            [.. file.Job.Urls.Select(u => u.AbsoluteUri)], file.ChecksumMatched));
        Storage.Write("history.json", _history);
        if (HistoryPage.Visibility == Visibility.Visible) FilterHistory();
    }

    void FilterHistory()
    {
        string search = HistorySearch.Text.Trim();
        HistoryRows.Clear();
        foreach (var entry in _history.Where(e => search.Length == 0 || e.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                                                  || e.Links.Any(l => l.Contains(search, StringComparison.OrdinalIgnoreCase))))
            HistoryRows.Add(new HistoryRow(entry));
        HistoryInfo.Text = !_settings.KeepHistory
            ? "The history is switched off in Settings: finished downloads aren't recorded."
            : _history.Count == 0 ? "No finished downloads yet. They are listed here once done, kept on this PC only."
            : $"{Format.Plural(_history.Count, "finished download")}, kept on this PC only. Clearing it or removing one leaves the files where they are.";
    }

    void HistorySearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => FilterHistory();

    static HistoryEntry? EntryOf(object sender) => ((sender as FrameworkElement)?.DataContext as HistoryRow)?.Entry;

    void HistoryOpen_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender)?.Path is { } path && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else
            ShowHistoryNotice("The file isn't there anymore: it was moved or deleted.");
    }

    void HistoryFolder_Click(object sender, RoutedEventArgs e) => ShowInExplorer(EntryOf(sender)?.Path);

    void HistoryAgain_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not { } entry) return;
        foreach (string link in entry.Links) AddEntry(EntryKind.Link, link);
        ShowDownloads();
        ShowNotice(InfoBarSeverity.Informational, $"The links of {entry.Name}", "Press Check links. If the file is in the save folder, complete, it is only checked, not downloaded again.");
    }

    void HistoryCopy_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not { } entry) return;
        var package = new DataPackage();
        package.SetText(string.Join("\r\n", entry.Links));
        _ownCopy = true; // not taken back in by the clipboard watcher
        Clipboard.SetContent(package);
    }

    void HistoryRemove_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not { } entry) return;
        _history.Remove(entry);
        Storage.Write("history.json", _history);
        FilterHistory();
    }

    async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Clear the history?",
            Content = $"MirrorDL forgets {Format.Plural(_history.Count, "finished download")}. The files themselves stay where they are.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await _questions.WaitAsync(0) is false) return; // a download's question is open
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        finally
        {
            _questions.Release();
        }
        ClearHistory();
    }

    void ClearHistory()
    {
        _history.Clear();
        Storage.Delete("history.json");
        FilterHistory();
    }

    void ShowHistoryNotice(string text) => HistoryInfo.Text = text;
}
