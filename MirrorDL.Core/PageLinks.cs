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
    // Every repetition is limited, so that a big or broken page (4 MB of "<a" without ">") can't take minutes.
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);
    static readonly Regex AnchorStart = new(@"<a\b(?<attrs>[^>]{0,4000})>", RegexOptions.IgnoreCase, Budget);
    static readonly Regex Attribute = new(@"(?<name>[\w-]+)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>""']+))",
        RegexOptions.IgnoreCase, Budget);
    static readonly Regex LinkAttribute = new(
        @"\b(?:data-(?:url|href|link|download|file|src))\s*=\s*(?:""(?<value>[^""]{0,4000})""|'(?<value>[^']{0,4000})')", RegexOptions.IgnoreCase, Budget);
    static readonly Regex Refresh = new(
        @"<meta[^>]{0,400}http-equiv\s*=\s*[""']?refresh[^>]{0,400}content\s*=\s*[""'][^""']{0,40}url\s*=\s*(?<value>[^""'>]{1,4000})",
        RegexOptions.IgnoreCase, Budget);
    static readonly Regex Quoted = new(@"[""'](?<value>https?:(?:\\?/){2}[^""'\s<>]{1,4000})[""']", RegexOptions.IgnoreCase, Budget);
    static readonly Regex Tags = new("<[^>]{0,4000}>", RegexOptions.None, Budget);
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

        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            FindIn(html, Add, watch);
        }
        catch (RegexMatchTimeoutException)
        {
            // A page too big or too broken to read in time: what was found so far.
        }
        return [.. found.OrderByDescending(f => f.Value.Score).ThenBy(f => f.Value.Order).Take(most).Select(f => f.Key)];
    }

    static void FindIn(string html, Action<string?, int> add, System.Diagnostics.Stopwatch watch)
    {
        foreach (Match a in AnchorStart.Matches(html))
        {
            if (watch.Elapsed > Budget) return;
            var attrs = Attribute.Matches(a.Groups["attrs"].Value)
                .GroupBy(m => m.Groups["name"].Value.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => WebUtility.HtmlDecode(g.First().Groups["value"].Value));
            // The button's text: up to its end tag, the next link, or 2000 characters.
            int from = a.Index + a.Length, end = Math.Min(html.Length, from + 2000);
            int close = html.IndexOf("</a", from, end - from, StringComparison.OrdinalIgnoreCase);
            int next = html.IndexOf("<a", from, end - from, StringComparison.OrdinalIgnoreCase);
            int stop = new[] { close, next, end }.Where(i => i >= 0).Min();
            string text = WebUtility.HtmlDecode(Tags.Replace(html[from..stop], " "));
            // A link marked as a download, or a button that says so, is worth a look even without a file's ending.
            int score = 1;
            if (DownloadAttribute.IsMatch(Values.Replace(a.Groups["attrs"].Value, ""))) score += 3;
            if (Says(text, "download") || Says(attrs.GetValueOrDefault("id"), "download") || Says(attrs.GetValueOrDefault("class"), "download"))
                score += 2;
            add(attrs.GetValueOrDefault("href"), score);
            foreach (var key in attrs.Keys.Where(k => k.StartsWith("data-", StringComparison.Ordinal)))
                add(attrs[key], score);
        }
        foreach (Match m in LinkAttribute.Matches(html)) add(WebUtility.HtmlDecode(m.Groups["value"].Value), 1);
        foreach (Match m in Refresh.Matches(html)) add(WebUtility.HtmlDecode(m.Groups["value"].Value.Trim()), 3);
        foreach (Match m in Quoted.Matches(html)) add(WebUtility.HtmlDecode(m.Groups["value"].Value.Replace("\\/", "/")), 1);
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
