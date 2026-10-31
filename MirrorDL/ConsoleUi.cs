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
        if (job.PartFile != null)
            Console.WriteLine($"Continuing: {job.PartFile}");
        Console.WriteLine("Paste the download links for one file (one per line), then press Enter on an empty line.");
        Dim("You can also paste a checksum (MD5/SHA-1/SHA-256/SHA-512), drag in an unfinished .part file");
        Dim("(from mirrordl, Firefox or aria2) to continue it, or drag in a folder to save the file there.");
        Console.WriteLine();
        while (Console.ReadLine() is { } line)
        {
            if (line.Trim().Length > 0)
            {
                Input.Add(line, job, ignored);
                continue;
            }
            if (job.Urls.Count == 0) continue;
            // Pasted text may contain empty lines: only start when nothing more is waiting.
            Thread.Sleep(150);
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

    /// <summary>The progress block: totals like curl and aria2 show them, then one line per link.</summary>
    public static List<string> StatusLines(Status s, Phase phase, Job job)
    {
        if (phase == Phase.Verifying)
        {
            string names = string.Join(" + ", job.Checksums.Select(c => Checksum.Algorithm(c)!.Value.Name));
            return [$"Checking {names}  {Bar(s.VerifyPercent, 30)} {Format.Percent(s.VerifyPercent),6}"];
        }

        string total = s.Size is long size ? $"{Format.Bytes(s.Have)} of {Format.Bytes(size)}" : Format.Bytes(s.Have);
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
}
