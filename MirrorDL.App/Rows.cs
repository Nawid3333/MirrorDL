// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MirrorDL.App;

/// <summary>The theme brushes for good, warning, error and secondary text.</summary>
static class Brushes
{
    public static Brush Normal => Get("TextFillColorPrimaryBrush");
    public static Brush Secondary => Get("TextFillColorSecondaryBrush");
    public static Brush Good => Get("SystemFillColorSuccessBrush");
    public static Brush Caution => Get("SystemFillColorCautionBrush");
    public static Brush Critical => Get("SystemFillColorCriticalBrush");
    public static Brush Accent => Get("AccentFillColorDefaultBrush");

    public static Brush For(LogKind kind) => kind switch
    {
        LogKind.Ok => Good,
        LogKind.Warn => Caution,
        LogKind.Error => Critical,
        LogKind.Dim => Secondary,
        _ => Normal,
    };

    static Brush Get(string key) => (Brush)Application.Current.Resources[key];
}

/// <summary>Raises <see cref="PropertyChanged"/> for x:Bind.</summary>
public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class LogLine(string text, Brush brush)
{
    public string Text { get; } = text;
    public Brush Brush { get; } = brush;
}

/// <summary>What a box of the new download holds.</summary>
public enum EntryKind { Link, Checksum, Unfinished }

/// <summary>One box of the new download: a link, a checksum or an unfinished file, and what MirrorDL makes of it.</summary>
public sealed class EntryRow(EntryKind kind, string text = "") : Bindable
{
    string _text = text;

    public EntryKind Kind { get; } = kind;
    /// <summary>Raised when the text changes, so that the list can keep an empty box at its end.</summary>
    public event Action<EntryRow>? TextChanged;

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value)) return;
            Changed(nameof(Hint));
            Changed(nameof(HintBrush));
            Changed(nameof(RemoveVisibility));
            TextChanged?.Invoke(this);
        }
    }

    public string Placeholder => Kind switch
    {
        EntryKind.Link => "Paste a link (https://…), or several at once",
        EntryKind.Checksum => "Paste a checksum, also with its file name (\"<checksum>  file.iso\")",
        _ => "Paste the path of an unfinished download, or use Browse…",
    };

    public Visibility RemoveVisibility => _text.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string Hint => Describe().Text;
    public Brush HintBrush => Describe().Brush;

    (string Text, Brush Brush) Describe()
    {
        string text = _text.Trim().Trim('"');
        if (text.Length == 0) return ("", Brushes.Secondary);
        switch (Kind)
        {
            case EntryKind.Link:
                return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    ? (uri.Host, Brushes.Secondary)
                    : ("not a link", Brushes.Caution);
            case EntryKind.Checksum:
                if (Input.NamedChecksum(text) is { } named)
                    return ($"{Checksum.Algorithm(named.Hex)!.Value.Name} of {Format.Clip(named.Name ?? "", 24)}", Brushes.Secondary);
                string first = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
                return Checksum.TryParse(first, out string? hex)
                    ? (Checksum.Algorithm(hex)!.Value.Name!, Brushes.Secondary)
                    : ("not a checksum", Brushes.Caution);
            default:
                try
                {
                    return File.Exists(text) ? (Format.Bytes(new FileInfo(text).Length), Brushes.Secondary) : ("not found", Brushes.Caution);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    return ("can't be read", Brushes.Caution);
                }
        }
    }
}

/// <summary>A file the links lead to, before it is downloaded: the user ticks what is to be downloaded.</summary>
public sealed class ReviewRow : Bindable
{
    bool _selected;

    public ReviewRow(BatchFile file)
    {
        File = file;
        var used = file.Links.Where(m => m.Usable).ToList();
        _selected = used.Count > 0 && !file.IsPage;
        FileName = file.Name;
        Size = file.Size is long size ? Format.Bytes(size) : used.Count == 0 ? "" : "size unknown";
        Links = Format.Plural(file.Links.Count, "link");
        var notes = new List<string>();
        if (file.IsPage) notes.Add("a web page with no link to a file on it that MirrorDL can see: open it, solve its captcha if it has one, and click its download button");
        else if (used.Count == 0) notes.Add("no link works: " + (file.Links.FirstOrDefault()?.Problem ?? ""));
        foreach (var m in file.Links.Where(m => m.FoundOn != null).Select(m => m.FoundOn!.Host).Distinct()) notes.Add($"found on {m}");
        int broken = file.Links.Count(m => !m.Usable);
        if (broken > 0 && used.Count > 0) notes.Add($"{broken} of them {(broken == 1 ? "doesn't" : "don't")} work");
        if (file.Links.Any(m => m.Usable && !m.SupportsRanges)) notes.Add("a server can't send parts: one connection, can't be paused");
        if (file.Size is > 0 && used.Count > 0 && new FileInfo(Path.Combine(file.Folder, file.Name)) is { Exists: true } there && there.Length == file.Size)
            notes.Add("in the save folder already: it is only checked, not downloaded again");
        if (file.Job.Checksums.Count > 0 && !file.Job.ChecksumsOfSeveral) notes.Add("checked by its checksum");
        if (file.Job.PartFiles.Count > 0) notes.Add($"continues {string.Join(", ", file.Job.PartFiles.Select(Path.GetFileName))}");
        Note = string.Join(" · ", notes);
        NoteBrush = file.IsPage || used.Count == 0 ? Brushes.Caution : Brushes.Secondary;
        PageVisibility = file.IsPage ? Visibility.Visible : Visibility.Collapsed;
        Tip = string.Join("\n", file.Links.Select(m => $"#{m.Index} {m.Url}" + (m.Problem != null ? $" - {m.Problem}" : "")));
    }

    public BatchFile File { get; }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public string FileName { get; }
    public string Size { get; }
    public string Links { get; }
    public string Note { get; }
    public Brush NoteBrush { get; }
    public Visibility PageVisibility { get; }
    public string Tip { get; }
}

/// <summary>One file in the download list.</summary>
public sealed class DownloadRow : Bindable
{
    string _glyph = "", _percentLabel = "", _speed = "", _left = "", _status = "waiting", _sizeText = "";
    double _percent;
    bool _indeterminate, _paused, _error;
    Brush _statusBrush = Brushes.Secondary;
    Visibility _pauseVisibility = Visibility.Collapsed, _resumeVisibility = Visibility.Collapsed,
               _folderVisibility = Visibility.Collapsed, _removeVisibility = Visibility.Visible;

    internal DownloadRow(Run run, BatchFile file)
    {
        Run = run;
        File = file;
        _sizeText = file.Size is long size ? Format.Bytes(size) : "";
    }

    internal Run Run { get; }
    public BatchFile File { get; }
    /// <summary>The file's own messages; those of its batch are in <see cref="Run.Log"/>.</summary>
    public ObservableCollection<LogLine> Log { get; } = [];
    /// <summary>Its numbers when they were last read; kept once it ended.</summary>
    public Status? Status { get; set; }

    public string FileName => File.Name;
    public string Links => Format.Plural(File.Links.Count, "link");
    public string Glyph { get => _glyph; private set => Set(ref _glyph, value); }
    public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    public string PercentLabel { get => _percentLabel; private set => Set(ref _percentLabel, value); }
    public bool Indeterminate { get => _indeterminate; private set => Set(ref _indeterminate, value); }
    public bool ShowPaused { get => _paused; private set => Set(ref _paused, value); }
    public bool ShowError { get => _error; private set => Set(ref _error, value); }
    public string Speed { get => _speed; private set => Set(ref _speed, value); }
    public string Left { get => _left; private set => Set(ref _left, value); }
    public string StatusText { get => _status; private set => Set(ref _status, value); }
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    public Visibility PauseVisibility { get => _pauseVisibility; private set => Set(ref _pauseVisibility, value); }
    public Visibility ResumeVisibility { get => _resumeVisibility; private set => Set(ref _resumeVisibility, value); }
    public Visibility FolderVisibility { get => _folderVisibility; private set => Set(ref _folderVisibility, value); }
    public Visibility RemoveVisibility { get => _removeVisibility; private set => Set(ref _removeVisibility, value); }

    /// <summary>Shows where the file is: <see cref="Status"/> while it is downloaded, its state otherwise.</summary>
    public void Update()
    {
        var file = File;
        var s = file.State == FileState.Running ? Status : null;
        var phase = s?.Phase ?? Phase.Starting;
        if (file.Size is long size) SizeText = Format.Bytes(size);
        else if (s?.Size is long known) SizeText = Format.Bytes(known);

        double percent = file.IsFinished ? 100
            : s is { } now ? (now.Phase == Phase.Verifying ? now.VerifyPercent : now.Percent)
            : Status?.Percent is > 0 and var shown ? shown
            : file.Have is long have && file.Size is > 0 ? have * 100.0 / file.Size.Value : 0;
        Percent = percent;
        PercentLabel = file.IsFinished || s?.Size is > 0 || Status?.Size is > 0 || file.Have > 0 && file.Size > 0 ? Format.Percent(percent) : "";
        Indeterminate = s != null && phase is Phase.Starting or Phase.Checking or Phase.Comparing or Phase.Finishing;
        ShowPaused = file.State is FileState.Paused || file.State == FileState.Waiting && !Run.IsRunning;
        ShowError = file.State is FileState.Failed or FileState.Mismatch;
        Speed = s is { Phase: Phase.Downloading } d ? Format.Speed(d.Speed) : "";
        Left = s is { Phase: Phase.Downloading, SecondsLeft: double left } ? Format.Duration(left) : "";

        (Glyph, StatusText, StatusBrush) = file.State switch
        {
            FileState.Running => ("", Describe(s), Brushes.Normal),
            FileState.Done => ("", "done", Brushes.Good),
            FileState.Checked => ("", "was there already: checked", Brushes.Good),
            FileState.Mismatch => ("", file.CheckedOnly ? "complete, but doesn't match" : "saved, but doesn't match", Brushes.Caution),
            FileState.Paused => ("", "paused", Brushes.Secondary),
            FileState.Failed => ("", file.IsPage ? "a web page: open it" : "failed: " + Format.Clip(file.Message ?? "", 80), Brushes.Critical),
            _ => ("", Run.IsRunning ? "waiting for its turn" : "waiting", Brushes.Secondary),
        };
        bool running = Run.IsRunning;
        PauseVisibility = running && file.State is FileState.Running or FileState.Waiting ? Visibility.Visible : Visibility.Collapsed;
        ResumeVisibility = !running && !file.IsFinished && !file.IsPage ? Visibility.Visible : Visibility.Collapsed;
        FolderVisibility = file.IsFinished && file.SavedPath != null ? Visibility.Visible : Visibility.Collapsed;
        RemoveVisibility = file.State == FileState.Running ? Visibility.Collapsed : Visibility.Visible;
    }

    static string Describe(Status? s) => s?.Phase switch
    {
        null or Phase.Starting or Phase.Checking => "checking the links…",
        Phase.Comparing => "comparing the content of the links…",
        Phase.Copying => "copying from unfinished files…",
        Phase.Downloading => $"downloading from {Format.Plural(s.Links.Count(l => l.Connections > 0), "link")}, {s.Connections} connections",
        Phase.Verifying => "checking the checksum…",
        Phase.Finishing => "finishing…",
        _ => "",
    };
}

/// <summary>A finished download in the History.</summary>
public sealed class HistoryRow
{
    internal HistoryRow(HistoryEntry entry)
    {
        Entry = entry;
        Size = entry.Size is long size ? Format.Bytes(size) : "";
        When = entry.Finished.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
        Outcome = entry.Result switch
        {
            FileState.Checked => "was there already",
            FileState.Mismatch => "doesn't match",
            _ => entry.Checksum != null ? "checksum matched" : "done",
        };
        OutcomeBrush = entry.Result == FileState.Mismatch ? Brushes.Caution : Brushes.Good;
        Folder = entry.Path is { } path ? Path.GetDirectoryName(path) ?? "" : "";
        Missing = entry.Path is { } p && !System.IO.File.Exists(p);
    }

    internal HistoryEntry Entry { get; }
    public string FileName => Entry.Name;
    public string Size { get; }
    public string When { get; }
    public string Outcome { get; }
    public Brush OutcomeBrush { get; }
    public string Folder { get; }
    public bool Missing { get; }
    public double Opacity => Missing ? 0.55 : 1;
    public string Tip => (Missing ? "The file isn't there anymore.\n" : "") + string.Join("\n", Entry.Links);
}

/// <summary>One row of the links table.</summary>
public sealed class LinkRow(Mirror link) : Bindable
{
    readonly Mirror _link = link;
    string _server = link.Server, _size = "", _connections = "", _speed = "", _downloaded = "", _status = "waiting";
    Brush _statusBrush = Brushes.Secondary;

    public string Number { get; } = $"#{link.Index}";
    public string Url { get; } = link.Url.ToString() + (link.FoundOn != null ? $"\nfound on {link.FoundOn}" : "");
    public string Server { get => _server; private set => Set(ref _server, value); }
    public string Size { get => _size; private set => Set(ref _size, value); }
    public string Connections { get => _connections; private set => Set(ref _connections, value); }
    public string Speed { get => _speed; private set => Set(ref _speed, value); }
    public string Downloaded { get => _downloaded; private set => Set(ref _downloaded, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }

    /// <summary>Its live numbers while the file downloads (<paramref name="s"/>), or what is known of the link otherwise.</summary>
    public void Update(LinkStatus? s, Phase? phase)
    {
        Server = _link.Server;
        Size = _link.Size is long size ? Format.Bytes(size) : "";
        int connections = s?.Connections ?? 0;
        Connections = connections > 0 ? connections.ToString() : "";
        Speed = connections > 0 ? Format.Speed(s!.Speed) : "";
        Downloaded = s?.Bytes > 0 ? Format.Bytes(s.Bytes) : "";
        (Status, StatusBrush) =
            _link.Problem != null ? (_link.Problem, Brushes.Caution)
            : s?.Retrying == true ? ("retrying: " + _link.LastError, Brushes.Caution)
            : !_link.Inspected ? (phase == null ? "waiting" : "checking…", Brushes.Secondary)
            : phase == Phase.Comparing && _link.Samples == null ? ("comparing content…", Brushes.Secondary)
            : connections > 0 ? ("downloading", Brushes.Normal)
            : _link.Note != null ? ("OK, but " + _link.Note, Brushes.Caution)
            : ("OK", Brushes.Good);
    }
}
