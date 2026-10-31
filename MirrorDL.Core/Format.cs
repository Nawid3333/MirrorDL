// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Globalization;

namespace MirrorDL;

/// <summary>Numbers, times and errors as short text.</summary>
public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : value.ToString("0.00", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public static string Speed(double bytesPerSecond) => Bytes((long)bytesPerSecond) + "/s";

    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds > 99 * 3600) return "--";
        var t = TimeSpan.FromSeconds(Math.Ceiling(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m"
             : t.TotalMinutes >= 1 ? $"{t.Minutes}m{t.Seconds:00}s"
             : $"{t.Seconds}s";
    }

    public static string Percent(double percent) => percent.ToString("0.0", CultureInfo.InvariantCulture) + "%";

    public static string Clip(string text, int width) => text.Length <= width ? text : text[..(width - 1)] + "…";

    public static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    /// <summary>A one-line error message without the "An error occurred while sending the request" noise.</summary>
    public static string Short(Exception e)
    {
        string message = e is SourceError or UserError ? e.Message : e.GetBaseException().Message;
        return Clip(message.ReplaceLineEndings(" ").Trim().TrimEnd('.'), 70);
    }
}

/// <summary>Bytes per second over the last few seconds.</summary>
sealed class SpeedMeter
{
    readonly Queue<(long Time, long Bytes)> _samples = new();

    public double Update(long totalBytes)
    {
        long now = Environment.TickCount64;
        _samples.Enqueue((now, totalBytes));
        while (_samples.Count > 2 && now - _samples.Peek().Time > 5000) _samples.Dequeue();
        var (time, bytes) = _samples.Peek();
        return now > time ? (totalBytes - bytes) * 1000.0 / (now - time) : 0;
    }
}
