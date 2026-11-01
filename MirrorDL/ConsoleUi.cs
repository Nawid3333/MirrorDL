// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

namespace MirrorDL;

static class Ui
{
    public static void Ok(string text) => Line(text, ConsoleColor.Green);
    public static void Warn(string text) => Line(text, ConsoleColor.Yellow);
    public static void Error(string text) => Line(text, ConsoleColor.Red);
    public static void Dim(string text) => Line(text, ConsoleColor.DarkGray);

    public static void Line(string text, LogKind kind) => Line(text, kind switch
    {
        LogKind.Ok => ConsoleColor.Green,
        LogKind.Warn => ConsoleColor.Yellow,
        LogKind.Error => ConsoleColor.Red,
        LogKind.Dim => ConsoleColor.DarkGray,
        _ => Console.ForegroundColor,
    });

    public static void Line(string text, ConsoleColor color)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = old;
    }

    public static string Bar(double percent, int width)
    {
        int filled = (int)Math.Clamp(percent / 100 * width, 0, width);
        return "[" + new string('█', filled) + new string('░', width - filled) + "]";
    }

    public static bool Confirm(string question)
    {
        if (Console.IsInputRedirected) return false;
        Console.Write(question + " [y/N] ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
    }

    public static void ReadInteractive(Job job, List<string> ignored)
    {
        foreach (string part in job.PartFiles)
            Console.WriteLine($"Continuing: {part}");
        Console.WriteLine("Paste the download links (one per line), then press Enter on an empty line.");
        Dim("Links to the same file are used together; links to other files are downloaded one after another.");
        Dim("You can also paste checksums (MD5/SHA-1/SHA-256/SHA-384/SHA-512, also as \"<checksum>  <file name>\"), drag");
        Dim("in unfinished downloads (from mirrordl, Firefox, aria2, any name) to continue them, or a folder to save in.");
        Console.WriteLine();
        while (Console.ReadLine() is { } line)
        {
            if (line.Trim().Length > 0)
            {
                Input.Add(line, job, ignored);
                continue;
            }
            if (job.Urls.Count == 0)
            {
                Dim("No link yet: paste at least one that starts with http:// or https://.");
                continue;
            }
            // Pasted text may contain empty lines: only start once nothing more arrives for a moment.
            for (int i = 0; i < 8 && !Console.KeyAvailable; i++) Thread.Sleep(50);
            if (!Console.KeyAvailable) return;
        }
    }

    public static void PrintLinks(IEnumerable<Mirror> mirrors)
    {
        Console.WriteLine();
        foreach (var m in mirrors)
        {
            Console.Write($"  {Format.Clip(m.Label, 44),-44} {(m.Size is long s ? Format.Bytes(s) : ""),10}  ");
            if (m.Problem != null) Line(m.Problem, ConsoleColor.Yellow);
            else if (m.Note != null) Line("OK, but " + m.Note, ConsoleColor.Yellow);
            else Line("OK", ConsoleColor.Green);
        }
        Console.WriteLine();
    }

    /// <summary>The files the links lead to, each with its links.</summary>
    public static void PrintFiles(IReadOnlyList<BatchFile> files)
    {
        Console.WriteLine();
        Console.WriteLine($"The links lead to {files.Count} files:");
        foreach (var f in files)
        {
            string links = string.Join(" ", f.Links.Select(m => $"#{m.Index}"));
            string size = f.Size is long s ? Format.Bytes(s) : "";
            Console.Write($"  {f.Number,3}  {Format.Clip(f.Name, 44),-44} {size,10}  ");
            if (f.Links.All(m => !m.Usable)) Line($"{links}  no link works", ConsoleColor.Yellow);
            else Dim(links);
        }
    }

    /// <summary>A line above the messages of a file.</summary>
    public static void FileHeader(BatchFile file, int count)
    {
        string title = $"── File {file.Number} of {count}: {file.Name} ";
        Console.WriteLine();
        Line(title + new string('─', Math.Max(3, 76 - title.Length)), ConsoleColor.Cyan);
    }

    /// <summary>How far all files are: done ones in full, the one being downloaded as far as it got.</summary>
    public static string BatchLine(IReadOnlyList<BatchFile> files, BatchFile current, Status status)
    {
        long total = files.Sum(f => f.Size ?? 0);
        long done = files.Where(f => f.IsFinished).Sum(f => f.Size ?? 0) + status.Have;
        int finished = files.Count(f => f.IsFinished);
        string all = total > 0 ? $"{Format.Percent(done * 100.0 / total)} - {Format.Bytes(done)} of {Format.Bytes(total)}" : "";
        string left = status.Speed > 0 && total > done ? $" - about {Format.Duration((total - done) / status.Speed)} left" : "";
        return $"File {current.Number} of {files.Count} ({finished} done)   all: {all}{left}";
    }

    /// <summary>How each file went.</summary>
    public static void PrintSummary(IReadOnlyList<BatchFile> files)
    {
        Console.WriteLine();
        Console.WriteLine(Batch.Summary(files.Where(f => f.State != FileState.Removed).ToList()));
        foreach (var f in files.Where(f => f.State != FileState.Removed))
        {
            var (mark, color, text) = f.State switch
            {
                FileState.Done => ("ok", ConsoleColor.Green, f.SavedPath ?? ""),
                FileState.Checked => ("ok", ConsoleColor.Green, "there already: " + f.SavedPath),
                FileState.Mismatch => ("!!", ConsoleColor.Yellow, "doesn't match: " + f.SavedPath),
                FileState.Paused => ("..", ConsoleColor.Yellow, "paused"),
                FileState.Failed => ("xx", ConsoleColor.Red, f.Message ?? "failed"),
                _ => ("..", ConsoleColor.DarkGray, "not started"),
            };
            Console.Write($"  {mark} {f.Number,3}  {Format.Clip(f.Name, 40),-40}  ");
            Line(text, color);
        }
    }

    /// <summary>The progress block: totals like curl and aria2 show them, then one line per link.</summary>
    public static List<string> StatusLines(Status s, Phase phase, Job job)
    {
        if (phase == Phase.Verifying)
        {
            string names = string.Join(" + ", job.Checksums.Select(c => Checksum.Algorithm(c)!.Value.Name));
            return [$"Checking {names}  {Bar(s.VerifyPercent, 30)} {Format.Percent(s.VerifyPercent),6}"];
        }

        string total = s.Size is long size ? $"{Format.Bytes(s.Have)} of {Format.Bytes(size)}" : Format.Bytes(s.Have);
        if (phase == Phase.Copying)
            return [$"Copying from the other unfinished files  {Bar(s.Percent, 30)} {Format.Percent(s.Percent),6}  {total}"];
        string left = s.SecondsLeft is double seconds ? Format.Duration(seconds) : "--";
        var lines = new List<string>
        {
            $"{Bar(s.Percent, 30)} {Format.Percent(s.Percent),6}  {total}",
            $"  speed {Format.Speed(s.Speed)}  average {Format.Speed(s.AverageSpeed)}  time {Format.Duration(s.Elapsed.TotalSeconds)}" +
            $"  left {left}  connections {s.Connections}",
        };
        foreach (var link in s.Links.Where(l => l.Bytes > 0 || l.Link.Usable))
        {
            var m = link.Link;
            string note = m.Problem != null ? "stopped: " + m.Problem : link.Retrying ? "retrying: " + m.LastError : "";
            lines.Add($"  {Format.Clip(m.Label, 34),-34} {link.Connections,2} conn {Format.Speed(link.Speed),12} {Format.Bytes(link.Bytes),11}  {note}");
        }
        return lines;
    }
}

/// <summary>Redraws a block of status lines in place; prints a line every few seconds when the output is redirected.</summary>
sealed class ProgressView
{
    readonly bool _live = !Console.IsOutputRedirected;
    int _top = -1;
    int _count;
    int _width;
    long _nextPlain;
    IReadOnlyList<string>? _last;

    public void Draw(IReadOnlyList<string> lines)
    {
        if (!_live)
        {
            _last = lines;
            if (Environment.TickCount64 < _nextPlain) return;
            _nextPlain = Environment.TickCount64 + 5000;
            Console.WriteLine(lines[0]);
            return;
        }
        int width = Math.Max(40, WindowWidth()) - 1;
        // A resized window wraps what is already there anew, so the block isn't where it was anymore: start a
        // new one below instead of writing over the wrong lines.
        if (_top >= 0 && width != _width)
        {
            _top = -1;
            _count = 0;
        }
        _width = width;
        // A block taller than the window would scroll it, and every redraw would then land in the wrong place.
        int height = Math.Max(4, WindowHeight()) - 1;
        if (lines.Count > height) lines = [.. lines.Take(height - 1), $"  ... and {lines.Count - height + 1} more"];
        try
        {
            if (_top < 0) Console.CursorVisible = false;
            else Console.SetCursorPosition(0, _top);
        }
        catch (Exception e) when (e is IOException or ArgumentOutOfRangeException)
        {
            _top = -1; // window was resized: just start a new block
        }
        for (int i = 0; i < Math.Max(lines.Count, _count); i++)
        {
            string line = i < lines.Count ? lines[i] : "";
            Console.WriteLine(line.Length > width ? line[..width] : line.PadRight(width));
        }
        if (_top < 0) _top = Console.CursorTop - Math.Max(lines.Count, _count);
        _count = Math.Max(lines.Count, _count);
    }

    public void End()
    {
        if (!_live && _last != null)
            foreach (string line in _last) Console.WriteLine(line); // the final state, every line
        _last = null;
        if (_live && _top >= 0) Console.CursorVisible = true;
    }

    static int WindowWidth()
    {
        try { return Console.WindowWidth; }
        catch (IOException) { return 100; }
    }

    static int WindowHeight()
    {
        try { return Console.WindowHeight; }
        catch (IOException) { return 30; }
    }
}
