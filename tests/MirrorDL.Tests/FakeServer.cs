// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Net;
using System.Net.Http.Headers;

namespace MirrorDL.Tests;

/// <summary>
/// Web servers in memory for the tests of a whole download: every link serves a file - with range requests or
/// without, under a name of its own or none - and can fail on purpose. No network, no ports.
/// </summary>
sealed class FakeServer : HttpMessageHandler
{
    readonly Dictionary<string, FakeLink> _links = [];

    /// <summary>Connections still open on any link: after a download, every answer must have been closed.</summary>
    public int Open => _links.Values.Sum(l => Volatile.Read(ref l.Open));

    /// <summary>A link that serves <paramref name="content"/>; its address ends in "file.bin" unless given.</summary>
    public FakeLink Add(string url, byte[] content)
    {
        var link = new FakeLink(content);
        _links[new Uri(url).AbsoluteUri] = link;
        return link;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Yield(); // like a real server: never answers on the caller's stack
        if (!_links.TryGetValue(request.RequestUri!.AbsoluteUri, out var link))
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        int number = Interlocked.Increment(ref link.Requests);
        if (link.Answer?.Invoke(request, number) is { } answer)
        {
            answer.RequestMessage = request;
            return answer;
        }
        if (link.MaxConnections > 0 && Volatile.Read(ref link.Open) >= link.MaxConnections)
        {
            Interlocked.Increment(ref link.Refused); // as a host that allows only so many connections at once
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
        }

        byte[] data = link.Content;
        HttpResponseMessage response;
        if (link.Ranges && request.Headers.Range?.Ranges.FirstOrDefault() is { } range)
        {
            long from = range.From ?? 0, to = Math.Min(range.To ?? data.Length - 1, data.Length - 1);
            if (from >= data.Length)
            {
                response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(data.Length);
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = Body(link, data, from, to - from + 1) };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from + link.Shift, to + link.Shift, data.Length + link.Grow);
            }
        }
        else
            response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Body(link, data, 0, data.Length) };
        if (link.Name != null)
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = link.Name };
        if (link.ContentType != null)
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(link.ContentType);
        // As after following a redirect: the answer comes from another address.
        response.RequestMessage = link.FinalUrl == null ? request : new HttpRequestMessage(request.Method, link.FinalUrl);
        return response;
    }

    static HttpContent Body(FakeLink link, byte[] data, long from, long count)
    {
        Stream stream = new MemoryStream(data, (int)from, (int)count, writable: false);
        if (Interlocked.Exchange(ref link.DropAfter, 0) is > 0 and long drop)
            stream = new DroppingStream(stream, drop);
        if (Interlocked.Exchange(ref link.CutAfter, 0) is > 0 and long cut)
            stream = new MemoryStream(data, (int)from, (int)Math.Min(count, cut), writable: false); // Content-Length stays
        if (link.ChunkDelay > TimeSpan.Zero)
            stream = new SlowStream(stream, link.ChunkDelay);
        Interlocked.Increment(ref link.Open);
        stream = new OpenStream(stream, link);
        var content = new StreamContent(stream);
        content.Headers.ContentLength = count;
        return content;
    }

    /// <summary>A body that counts as an open connection of its link until it is disposed.</summary>
    sealed class OpenStream(Stream inner, FakeLink link) : Stream
    {
        int _closed;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
            {
                Interlocked.Decrement(ref link.Open);
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body that sends at most 64 KB at a time, with a pause before each.</summary>
    sealed class SlowStream(Stream inner, TimeSpan delay) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(delay, ct);
            return await inner.ReadAsync(buffer[..Math.Min(buffer.Length, 64 * 1024)], ct);
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body that breaks off with a network error after <paramref name="limit"/> bytes.</summary>
    sealed class DroppingStream(Stream inner, long limit) : Stream
    {
        long _read;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= limit) throw new IOException("connection reset (test)");
            int n = inner.Read(buffer, offset, (int)Math.Min(count, limit - _read));
            _read += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>One link of a <see cref="FakeServer"/> and how it behaves.</summary>
sealed class FakeLink(byte[] content)
{
    public byte[] Content { get; set; } = content;
    /// <summary>Whether it sends parts of the file (206) or always the whole file (200).</summary>
    public bool Ranges { get; set; } = true;
    /// <summary>The file name it sends in Content-Disposition, if any.</summary>
    public string? Name { get; set; }
    /// <summary>The Content-Type it sends, if any.</summary>
    public string? ContentType { get; set; }
    /// <summary>Its own answer to request number n (from 1), instead of the file; null for the normal answer.</summary>
    public Func<HttpRequestMessage, int, HttpResponseMessage?>? Answer { get; set; }
    /// <summary>The next body breaks off after this many bytes (once); 0 for never.</summary>
    public long DropAfter;
    /// <summary>The next body ends after this many bytes (once), cleanly, though Content-Length says more; 0 for never.</summary>
    public long CutAfter;
    /// <summary>The address the answers come from, as after a redirect to it; null for the link's own.</summary>
    public Uri? FinalUrl { get; set; }
    /// <summary>How many connections may be open at once; more are answered with 503. 0 for no limit.</summary>
    public int MaxConnections { get; set; }
    public int Open, Refused;
    /// <summary>A pause before every 64 KB of a body: makes the link slow.</summary>
    public TimeSpan ChunkDelay { get; set; }
    /// <summary>Lies in Content-Range: the part said to start this much later, the file this much bigger.</summary>
    public long Shift { get; set; }
    public long Grow { get; set; }
    public int Requests;
}
