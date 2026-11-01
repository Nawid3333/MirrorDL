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
    public static Task<(string Version, Uri Page)?> FindNewerAsync(CancellationToken ct = default) =>
        FindNewerAsync(AppInfo.Version, null, ct);

    /// <summary>The same for <paramref name="version"/>; <paramref name="handler"/> answers instead of GitHub in tests.</summary>
    internal static async Task<(string Version, Uri Page)?> FindNewerAsync(string version, HttpMessageHandler? handler, CancellationToken ct)
    {
        if (!System.Version.TryParse(version, out var current)) return null; // "0.0.0-dev": a local build
        try
        {
            using var http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromSeconds(15);
            http.MaxResponseContentBufferSize = 1 << 20; // a release is a few KB; anything bigger is not GitHub's answer
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
                                      or KeyNotFoundException or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
