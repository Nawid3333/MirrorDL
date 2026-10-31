// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Net.Http.Headers;
using System.Text.Json;

namespace MirrorDL;

/// <summary>Asks GitHub whether a newer release exists. Only for the GitHub build: the Store updates its own.</summary>
public static class Updates
{
    const string LatestRelease = "https://api.github.com/repos/Nawid3333/MirrorDL/releases/latest";

    /// <summary>The newer version and its release page, or null: none, a local build, offline, or GitHub not answering.</summary>
    public static async Task<(string Version, Uri Page)?> FindNewerAsync(CancellationToken ct = default)
    {
        if (!System.Version.TryParse(AppInfo.Version, out var current)) return null; // "0.0.0-dev": a local build
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MirrorDL", AppInfo.Version));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var json = JsonDocument.Parse(await http.GetStringAsync(LatestRelease, ct));
            string tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
            string page = json.RootElement.GetProperty("html_url").GetString() ?? "";
            return System.Version.TryParse(tag.TrimStart('v'), out var latest) && latest > current
                && Uri.TryCreate(page, UriKind.Absolute, out var uri) && uri.Host == "github.com"
                ? (latest.ToString(3), uri)
                : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
                                      or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
