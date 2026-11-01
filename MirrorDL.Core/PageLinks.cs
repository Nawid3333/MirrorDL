// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Net;
using System.Text.RegularExpressions;

namespace MirrorDL;

/// <summary>
/// Finds the links to files on a web page - what its download buttons lead to - by reading the page as it comes
/// from the server, without running its scripts. A link a page only makes after a captcha can't be found this way:
/// the user opens such a page in the browser and clicks the button there.
/// </summary>
public static class PageLinks
{
    static readonly Regex Anchor = new(@"<a\b(?<attrs>[^>]*)>(?<text>.*?)</a\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    static readonly Regex Attribute = new(@"(?<name>[\w-]+)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>""']+))",
        RegexOptions.IgnoreCase);
    static readonly Regex LinkAttribute = new(
        @"\b(?:data-(?:url|href|link|download|file|src))\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)')", RegexOptions.IgnoreCase);
    static readonly Regex Refresh = new(@"<meta[^>]+http-equiv\s*=\s*[""']?refresh[^>]*content\s*=\s*[""'][^""']*url\s*=\s*(?<value>[^""'>]+)",
        RegexOptions.IgnoreCase);
    static readonly Regex Quoted = new(@"[""'](?<value>https?:(?:\\?/){2}[^""'\s<>]+)[""']", RegexOptions.IgnoreCase);
    static readonly Regex Tags = new("<[^>]+>");
    static readonly Regex Values = new(@"""[^""]*""|'[^']*'");
    static readonly Regex DownloadAttribute = new(@"(^|\s)download(\s|=|$)", RegexOptions.IgnoreCase);
    static readonly Regex PieceNumber = new(@"\.(?:r\d{2}|\d{3})$", RegexOptions.IgnoreCase);

    // Endings of files people download. Pages, scripts, styles, pictures and fonts of the page itself are not among them.
    static readonly HashSet<string> FileEndings = new(
        ["zip", "rar", "7z", "tar", "gz", "tgz", "xz", "txz", "bz2", "tbz2", "zst", "lz", "lzma", "cab", "arj", "lzh",
         "iso", "img", "bin", "cue", "nrg", "mdf", "vhd", "vhdx", "vmdk", "ova", "qcow2", "wim", "esd",
         "exe", "msi", "msix", "msixbundle", "appx", "appxbundle", "dmg", "pkg", "deb", "rpm", "apk", "xapk", "aab", "jar", "appimage", "snap", "flatpak",
         "mp4", "mkv", "avi", "mov", "wmv", "webm", "flv", "m4v", "mpg", "mpeg", "ts", "m2ts", "vob", "3gp",
         "mp3", "flac", "wav", "ogg", "opus", "m4a", "aac", "wma", "ape", "alac",
         "pdf", "epub", "mobi", "azw3", "djvu", "cbz", "cbr", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "csv",
         "torrent", "nsp", "xci", "nsz", "wbfs", "wad", "cia", "3ds", "pup", "ipa", "dll", "sys", "rom", "sfc", "nes", "gba"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Links on the page that look like they lead to a file, the most likely first: ones whose address ends in a file's
    /// ending, that are marked as downloads, or whose button says "download". At most <paramref name="most"/>.
    /// </summary>
    public static List<Uri> Find(string html, Uri page, int most = 20)
    {
        var found = new Dictionary<Uri, (int Score, int Order)>();
        void Add(string? raw, int score)
        {
            if (score <= 0 || Resolve(raw, page) is not { } uri) return;
            if (StripFragment(uri) == StripFragment(page)) return; // the page itself
            int extra = LooksLikeFile(uri) ? 3 : 0;
            int total = score + extra;
            if (total < 3) return;
            if (!found.TryGetValue(uri, out var had) || had.Score < total)
                found[uri] = (total, had.Score > 0 ? had.Order : found.Count);
        }

        foreach (Match a in Anchor.Matches(html))
        {
            var attrs = Attribute.Matches(a.Groups["attrs"].Value)
                .GroupBy(m => m.Groups["name"].Value.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => WebUtility.HtmlDecode(g.First().Groups["value"].Value));
            string text = WebUtility.HtmlDecode(Tags.Replace(a.Groups["text"].Value, " "));
            // A link marked as a download, or a button that says so, is worth a look even without a file's ending.
            int score = 1;
            if (DownloadAttribute.IsMatch(Values.Replace(a.Groups["attrs"].Value, ""))) score += 3;
            if (Says(text, "download") || Says(attrs.GetValueOrDefault("id"), "download") || Says(attrs.GetValueOrDefault("class"), "download"))
                score += 2;
            Add(attrs.GetValueOrDefault("href"), score);
            foreach (var key in attrs.Keys.Where(k => k.StartsWith("data-", StringComparison.Ordinal)))
                Add(attrs[key], score);
        }
        foreach (Match m in LinkAttribute.Matches(html)) Add(WebUtility.HtmlDecode(m.Groups["value"].Value), 1);
        foreach (Match m in Refresh.Matches(html)) Add(WebUtility.HtmlDecode(m.Groups["value"].Value.Trim()), 3);
        foreach (Match m in Quoted.Matches(html)) Add(WebUtility.HtmlDecode(m.Groups["value"].Value.Replace("\\/", "/")), 1);

        return [.. found.OrderByDescending(f => f.Value.Score).ThenBy(f => f.Value.Order).Take(most).Select(f => f.Key)];
    }

    /// <summary>Whether an address ends in the name of a file people download, also a piece of a split archive (".r00", ".001").</summary>
    public static bool LooksLikeFile(Uri uri)
    {
        string name = Uri.UnescapeDataString(uri.Segments[^1]);
        if (PieceNumber.IsMatch(name)) return true;
        int dot = name.LastIndexOf('.');
        return dot > 0 && FileEndings.Contains(name[(dot + 1)..]);
    }

    static bool Says(string? text, string word) => text?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;

    static Uri? Resolve(string? raw, Uri page)
    {
        raw = raw?.Trim();
        if (string.IsNullOrEmpty(raw) || raw.StartsWith('#')) return null;
        if (!Uri.TryCreate(page, raw, out var uri)) return null;
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    static string StripFragment(Uri uri) => uri.GetLeftPart(UriPartial.Query);
}
