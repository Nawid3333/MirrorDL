// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Text;
using System.Text.RegularExpressions;

namespace MirrorDL;

/// <summary>
/// Lists of links kept in a file: a .txt or .md file given or dropped is read for the links and checksums written in
/// it, like pasted text, instead of being continued as a download. Markdown's marks around them are read past.
/// </summary>
public static class LinkFiles
{
    /// <summary>Endings of the files that are lists of links; any other file given is an unfinished download.</summary>
    public static readonly IReadOnlyList<string> Endings = [".txt", ".md", ".markdown"];

    /// <summary>The biggest file read as a list: tens of thousands of links. A bigger one is something else.</summary>
    public const int MaxBytes = 4 << 20;

    // A picture, "![text](address)", is shown on the page, not listed for downloading.
    static readonly Regex Picture = new(@"!\[[^\]\r\n]*\]\([^)\r\n]*\)");
    // Before the text of a line: quote marks, a heading's #, a list item's mark and its task box.
    static readonly Regex LineMarks = new(@"^\s*(?:>\s*)*(?:#{1,6}\s+|(?:[-*+]|\d{1,9}[.)])\s+(?:\[[ xX]\]\s+)?)?");
    // "\_" stands for "_": markdown's way of writing a mark as itself.
    static readonly Regex Escape = new(@"\\(?=[!-/:-@\[-`{-~])");

    /// <summary>Whether a given file is a list of links: a .txt or .md file that aria2 or MirrorDL isn't downloading.</summary>
    public static bool Is(string path) =>
        Endings.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))
        && !File.Exists(path + ".aria2") && !File.Exists(path + ".mirrordl");

    /// <summary>
    /// The text of a list of links, to be read line by line like pasted text; a markdown file's marks taken out.
    /// Throws <see cref="InvalidDataException"/> for a file too big for a list or not text, and IO errors as they come.
    /// </summary>
    public static string Read(string path)
    {
        // Shared: a list open in an editor can still be read.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxBytes)
            throw new InvalidDataException($"The file is bigger than {MaxBytes >> 20} MB: too big for a list of links.");
        string text = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true).ReadToEnd();
        if (text.Contains('\0')) throw new InvalidDataException("The file isn't text.");
        return path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)
            ? Markdown(text)
            : text;
    }

    /// <summary>
    /// Markdown as plain lines: without pictures, the marks before a line's text, escapes, code marks, table bars and
    /// bold or italic stars - so that "- `&lt;checksum&gt;` file.iso" and "| file.iso | link |" read like pasted lines.
    /// Links written "[name](link)" or "&lt;link&gt;" stay: <see cref="Input.LinksIn"/> finds them.
    /// </summary>
    internal static string Markdown(string text)
    {
        var lines = Picture.Replace(text, " ").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = Escape.Replace(LineMarks.Replace(lines[i], ""), "");
            lines[i] = line.Replace('`', ' ').Replace('|', ' ').Replace('*', ' ');
        }
        return string.Join('\n', lines);
    }
}
