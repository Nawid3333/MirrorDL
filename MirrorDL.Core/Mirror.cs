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
    /// <summary>How many connections this link may have now (<see cref="Tuner"/> decides), and at most.</summary>
    public int Cap, Ceiling;
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
            else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && headers.ContentRange is { HasLength: true, Length: 0 })
            {
                Size = 0; // an empty file: it has no first byte to ask for
                SupportsRanges = true;
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
        try
        {
            for (int i = 0; i < offsets.Count; i++)
                samples[i] = await HashRangeAsync(http, offsets[i], length, ct);
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

    /// <summary>The SHA-256 of <paramref name="length"/> bytes at <paramref name="offset"/>, as this link sends them.</summary>
    internal async Task<byte[]> HashRangeAsync(HttpClient http, long offset, int length, CancellationToken ct) =>
        SHA256.HashData(await ReadRangeAsync(http, offset, length, ct));

    /// <summary><paramref name="length"/> bytes at <paramref name="offset"/>, as this link sends them.</summary>
    internal async Task<byte[]> ReadRangeAsync(HttpClient http, long offset, int length, CancellationToken ct)
    {
        if (length <= 0) return [];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new SourceError($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        if (response.Content.Headers.ContentRange?.From != offset)
            throw new SourceError("server sent the wrong part of the file");
        var buffer = new byte[length];
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        await body.ReadExactlyAsync(buffer, timeout.Token);
        return buffer;
    }

    // Names Windows keeps for devices, with or without an extension: a file "nul.iso" would be the NUL device.
    static readonly HashSet<string> DeviceNames = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(0, 10).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" }),
         "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>A file name a server sent, made safe: no folders, no invalid characters, no device name.</summary>
    internal static string CleanFileName(string name)
    {
        name = name.Trim().Trim('"');
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        char[] invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length == 0) return "download";
        string stem = name.Split('.')[0].TrimEnd();
        return DeviceNames.Contains(stem) ? "_" + name : name;
    }
}
