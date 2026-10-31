// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

namespace MirrorDL;

/// <summary>Sorts pasted text and arguments into links, checksums, unfinished files and an output folder.</summary>
public static class Input
{
    public static void Add(string line, Job job, List<string> ignored, bool allowFiles = true)
    {
        string text = Unquote(line.Trim());
        if (text.Length == 0) return;
        if (allowFiles && AddPath(text, job, ignored)) return; // a dragged-in path may contain spaces
        foreach (string token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = Unquote(token);
            if (Uri.TryCreate(t, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                if (!job.Urls.Contains(uri)) job.Urls.Add(uri);
            }
            else if (Checksum.TryParse(t, out string? hex))
            {
                if (!job.Checksums.Contains(hex)) job.Checksums.Add(hex);
            }
            else if (!(allowFiles && AddPath(t, job, ignored)))
            {
                ignored.Add(t);
            }
        }
    }

    static bool AddPath(string path, Job job, List<string> ignored)
    {
        if (path.Contains("://")) return false;
        try
        {
            if (Path.IsPathRooted(path) && Directory.Exists(path))
            {
                job.OutDir = Path.GetFullPath(path);
                return true;
            }
            if (!File.Exists(path)) return false;
            path = Path.GetFullPath(path);
            // Dragging in a progress file means the download next to it.
            foreach (string extension in new[] { ".aria2", ".mirrordl" })
                if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && File.Exists(path[..^extension.Length]))
                    path = path[..^extension.Length];

            // A .txt file holds links; any other file is an unfinished download of this file, whatever its name -
            // it is compared with the links before anything of it is used.
            if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && !File.Exists(path + ".aria2") && !File.Exists(path + ".mirrordl"))
                foreach (string line in File.ReadLines(path)) Add(line, job, ignored, allowFiles: false);
            else if (!job.PartFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
                job.PartFiles.Add(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    static string Unquote(string s) =>
        s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\'' && s[^1] == '\'') ? s[1..^1].Trim() : s;
}
