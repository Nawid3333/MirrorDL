// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace MirrorDL;

/// <summary>One of the pasted links: what we found out about it, and its live download numbers.</summary>
public sealed class Mirror(int index, Uri url)
{
    public const int SampleLength = 64 * 1024;
    static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    public int Index { get; } = index;
    public Uri Url { get; } = url;
    public Uri? FinalUrl { get; private set; }
    /// <summary>The server, and where it redirects to if that's another one.</summary>
    public string Server => FinalUrl == null || FinalUrl.Authority == Url.Authority
        ? Url.Authority
        : $"{Url.Authority} → {FinalUrl.Authority}";
    public string Label => $"#{Index} {Server}";

    public long? Size { get; private set; }
    public bool SupportsRanges { get; private set; }
    public string? FileName { get; private set; }
    public bool NameFromHeader { get; private set; }
    public string? Note { get; private set; }
    public byte[][]? Samples { get; private set; }
    public string Fingerprint => Samples == null ? "" : string.Join("|", Samples.Select(Convert.ToHexString));

    /// <summary>Why this link isn't used; null while it is.</summary>
    public volatile string? Problem;
    public bool Usable => Problem == null;

    // Live numbers, updated by several connections at once.
    public long Bytes;
    public int Workers;
    public int Cap;
    public int Errors;
    public long LastData;
    public long LastErrorAt;
    public volatile string? LastError;

    public void GotData(int count)
    {
        Interlocked.Add(ref Bytes, count);
        Volatile.Write(ref LastData, Environment.TickCount64);
        Volatile.Write(ref Errors, 0);
    }

    /// <summary>Asks for the first byte: that tells the size, whether parts can be requested, and the file name.</summary>
    internal async Task InspectAsync(HttpClient http, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            FinalUrl = response.RequestMessage?.RequestUri;
            var headers = response.Content.Headers;
            if (response.StatusCode == HttpStatusCode.PartialContent && headers.ContentRange is { HasLength: true } range)
            {
                Size = range.Length;
                SupportsRanges = true;
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                Size = headers.ContentLength;
            }
            else
            {
                Problem = response.IsSuccessStatusCode
                    ? "unexpected answer from the server"
                    : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                return;
            }

            string? name = headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName;
            NameFromHeader = !string.IsNullOrWhiteSpace(name);
            if (!NameFromHeader) name = Uri.UnescapeDataString((FinalUrl ?? Url).Segments[^1]);
            FileName = CleanFileName(name!);
            if (headers.ContentType?.MediaType == "text/html")
                Note = "looks like a web page, not a file";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Problem = "no answer within 30 s";
        }
        catch (HttpRequestException e)
        {
            Problem = Format.Short(e);
        }
    }

    /// <summary>Fetches small pieces at the given offsets and keeps their SHA-256, to compare links with each other.</summary>
    internal async Task SampleAsync(HttpClient http, IReadOnlyList<long> offsets, int length, CancellationToken ct)
    {
        var samples = new byte[offsets.Count][];
        var buffer = new byte[length];
        try
        {
            for (int i = 0; i < offsets.Count; i++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(CheckTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, Url);
                request.Headers.Range = new RangeHeaderValue(offsets[i], offsets[i] + length - 1);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode != HttpStatusCode.PartialContent)
                    throw new SourceError($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                if (response.Content.Headers.ContentRange?.From != offsets[i])
                    throw new SourceError("server sent the wrong part of the file");
                await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
                await body.ReadExactlyAsync(buffer, timeout.Token);
                samples[i] = SHA256.HashData(buffer);
            }
            Samples = samples;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Problem = "content check timed out";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or SourceError)
        {
            Problem = "content check failed: " + Format.Short(e);
        }
    }

    static string CleanFileName(string name)
    {
        name = name.Trim().Trim('"');
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        char[] invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return name.Length == 0 ? "download" : name;
    }
}
