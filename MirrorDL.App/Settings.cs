// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Text.Json;
using System.Text.Json.Serialization;

namespace MirrorDL.App;

/// <summary>
/// What the user switched on or off, and the download list and history, kept in %LOCALAPPDATA%\MirrorDL on this PC
/// only. Every file is written whole to a new file first and then swapped in, so a crash never leaves half of one.
/// </summary>
sealed class Settings
{
    // Downloads
    public bool SortLinks { get; set; } = true;
    public bool LookIntoPages { get; set; } = true;
    public bool Review { get; set; } = true;
    public bool KeepAwake { get; set; } = true;
    public bool DeleteLeftovers { get; set; }
    public int? Connections { get; set; }
    public int? PerLink { get; set; }
    // Links from elsewhere
    public bool ClickNLoad { get; set; } = true;
    public bool Browser { get; set; } = true;
    public bool WatchClipboard { get; set; }
    // Window
    public bool TaskbarProgress { get; set; } = true;
    public bool FlashWhenDone { get; set; } = true;
    // What is kept
    public bool KeepList { get; set; } = true;
    public bool ResumeAtStart { get; set; }
    public bool KeepHistory { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    public string? SaveTo { get; set; }

    public static Settings Load() => Storage.Read<Settings>("settings.json") ?? new Settings();
    public void Save() => Storage.Write("settings.json", this);
}

/// <summary>A finished download, as the History shows it.</summary>
sealed record HistoryEntry(string Name, long? Size, string? Path, DateTime Finished, FileState Result, List<string> Links,
    string? Checksum);

/// <summary>Files in %LOCALAPPDATA%\MirrorDL.</summary>
static class Storage
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MirrorDL");

    public static T? Read<T>(string name) where T : class
    {
        try
        {
            string path = Path.Combine(Folder, name);
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null; // damaged or unreadable: started anew rather than not at all
        }
    }

    public static void Write<T>(string name, T value)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, name), temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Kept for the next try: the next change writes it again.
        }
    }

    public static void Delete(string name)
    {
        try { File.Delete(Path.Combine(Folder, name)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
