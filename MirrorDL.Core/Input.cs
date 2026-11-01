// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Text.RegularExpressions;

namespace MirrorDL;

/// <summary>Sorts pasted text and arguments into links, checksums, unfinished files and an output folder.</summary>
public static class Input
{
    // A checksum with the file's name on its line, as checksum files and download pages write them:
    // "SHA256 (name) = hex" (BSD), "hex  name" or "hex *name" (sha256sum), "name: hex" or "name hex".
    static readonly Regex Bsd = new(@"^(?:MD5|SHA-?1|SHA-?256|SHA-?384|SHA-?512)\s*\((?<name>.+)\)\s*=\s*(?<hex>[0-9A-Fa-f]+)$", RegexOptions.IgnoreCase);
    static readonly Regex HexFirst = new(@"^(?<hex>[0-9A-Fa-f]{32,128})\s+\*?(?<name>.+)$");
    static readonly Regex NameFirst = new(@"^(?<name>.+?)[\s:=]+(?<hex>[0-9A-Fa-f]{32,128})$");
    // What a page writes before a checksum instead of a file name.
    static readonly HashSet<string> NotNames = new(
        ["md5", "sha1", "sha-1", "sha256", "sha-256", "sha384", "sha-384", "sha512", "sha-512", "checksum", "hash", "sum",
         "md5sum", "sha1sum", "sha256sum", "sha384sum", "sha512sum"],
        StringComparer.OrdinalIgnoreCase);

    public static void Add(string line, Job job, List<string> ignored, bool allowFiles = true)
    {
        string text = Unquote(line.Trim());
        if (text.Length == 0) return;
        if (allowFiles && AddPath(text, job, ignored)) return; // a dragged-in path may contain spaces
        if (NamedChecksum(text) is { } named)
        {
            if (!job.FileChecksums.Contains(named)) job.FileChecksums.Add(named);
            return;
        }
        Uri? link = null;
        var checksums = new List<string>();
        foreach (string token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = Unquote(token);
            if (Uri.TryCreate(t, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                if (!job.Urls.Contains(uri)) job.Urls.Add(uri);
                link ??= uri;
            }
            else if (Checksum.TryParse(t, out string? hex))
            {
                checksums.Add(hex);
            }
            else if (!(allowFiles && AddPath(t, job, ignored)))
            {
                ignored.Add(t);
            }
        }
        // A checksum on the line of a link is the checksum of that link's file.
        foreach (string hex in checksums)
        {
            if (link != null)
            {
                var linked = new FileChecksum(hex, Link: link);
                if (!job.FileChecksums.Contains(linked)) job.FileChecksums.Add(linked);
            }
            else if (!job.Checksums.Contains(hex))
                job.Checksums.Add(hex);
        }
    }

    /// <summary>A checksum and the name of its file, written on one line; null if the line isn't one.</summary>
    public static FileChecksum? NamedChecksum(string line)
    {
        var match = Bsd.Match(line);
        if (!match.Success) match = HexFirst.Match(line);
        if (!match.Success) match = NameFirst.Match(line);
        if (!match.Success) return null;
        string hex = match.Groups["hex"].Value.ToLowerInvariant();
        string name = match.Groups["name"].Value.Trim().TrimEnd(':').Trim();
        if (Checksum.Algorithm(hex) == null || name.Length == 0 || name.Contains("://") || NotNames.Contains(name)
            || Checksum.TryParse(name, out _))
            return null;
        // Only a file's name: "Commit: <hash>" or "Mirror 2 checksum = <hash>" are words before a checksum.
        name = Mirror.CleanFileName(name);
        return Batch.IsFileName(name) ? new FileChecksum(hex, name) : null;
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
