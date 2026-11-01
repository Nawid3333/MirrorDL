// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;

namespace MirrorDL;

/// <summary>An error from one link. <see cref="Status"/> is the HTTP status, 0 for network errors.</summary>
sealed class SourceError(string message, int status = 0, bool fatal = false) : Exception(message)
{
    public int Status { get; } = status;
    public bool Fatal { get; } = fatal;
}

/// <summary>Writing to the file failed (disk full, drive gone): no link can help with that, so everything stops.</summary>
sealed class WriteError(IOException inner) : Exception(inner.Message, inner);

/// <summary>
/// Downloads the missing ranges of one file from several identical links at once. Every connection works
/// on a segment of its own; a connection without work takes a free range or splits the biggest unfinished
/// segment in half, so fast links keep taking work over from slow ones until the very end. How many
/// connections each link gets, <see cref="Tuner"/> decides; <paramref name="maxConnections"/> and
/// <paramref name="perLink"/> are limits on top, null for none.
/// </summary>
sealed class Engine(HttpClient http, SafeFileHandle file, long size, IEnumerable<(long Start, long End)> missing,
                    IReadOnlyList<Mirror> mirrors, int? maxConnections, int? perLink)
{
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    const int BufferSize = 256 * 1024;
    const long MinSplit = 1 << 20;
    const int BusyRetries = 8; // "busy" answers in a row a connection waits out before it leaves the link
    static readonly TimeSpan EndgameWait = TimeSpan.FromSeconds(5);

    enum Next { Retry, Stop, Left }

    sealed class Segment(long start, long end)
    {
        public long Pos = start;      // everything before Pos is written
        public long Reserved = start; // everything before Reserved is written or being written
        public long End = end;        // shrinks when another connection takes over the second half
        public readonly long Start = start, Since = Environment.TickCount64; // to tell how fast it goes
        // Where it was a second or two ago, for its speed of the last seconds (see Lagging).
        public long MarkPos = start, MarkTime = Environment.TickCount64, EarlierPos = start, EarlierTime = Environment.TickCount64;

        /// <summary>Notes the progress at most once a second: the last mark and the one before it.</summary>
        public void Mark(long now)
        {
            if (now - MarkTime < 1000) return;
            (EarlierPos, EarlierTime, MarkPos, MarkTime) = (MarkPos, MarkTime, Pos, now);
        }
    }

    readonly object _lock = new();
    readonly List<(long Start, long End)> _free = Ranges.Normalize(missing);
    readonly List<Segment> _active = [];
    readonly List<Task> _workers = [];
    readonly int _maxTotal = maxConnections ?? int.MaxValue;
    CancellationTokenSource? _stop;

    /// <summary>Why writing to the file failed, if it did; the download then stopped.</summary>
    public IOException? WriteFailure { get; private set; }

    public bool IsComplete
    {
        get { lock (_lock) return _free.Count == 0 && _active.Count == 0; }
    }

    public long RemainingBytes
    {
        get { lock (_lock) return Ranges.Sum(_free) + _active.Sum(s => s.End - s.Pos); }
    }

    /// <summary>The ranges not written yet.</summary>
    public List<(long Start, long End)> Remaining()
    {
        lock (_lock)
            return Ranges.Normalize(_free.Concat(_active.Select(s => (s.Pos, s.End))));
    }

    /// <summary>Runs until the file is complete, every link failed, or <paramref name="ct"/> is cancelled.</summary>
    public async Task<bool> RunAsync(Action tick, CancellationToken ct)
    {
        var tuner = new Tuner(mirrors, perLink ?? Tuner.MaxPerLink, _maxTotal);
        using var stop = _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            while (!IsComplete && !ct.IsCancellationRequested && WriteFailure == null)
            {
                tuner.Update(Environment.TickCount64, RemainingBytes);
                StartWorkers(stop.Token);
                if (_workers.Count == 0) break; // every link failed
                tick();
                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(_workers);
        }
        tick();
        return IsComplete;
    }

    /// <summary>
    /// Gives every link the connections it may have, taking turns between the links up to the total limit.
    /// A link gets one connection until data has arrived over it, so that a host refusing extra connections
    /// can't get the whole link dropped.
    /// </summary>
    void StartWorkers(CancellationToken ct)
    {
        _workers.RemoveAll(t => t.IsCompleted);
        int running = mirrors.Sum(m => Volatile.Read(ref m.Workers));
        bool added = true;
        while (added && running < _maxTotal)
        {
            added = false;
            foreach (var m in mirrors)
            {
                if (running >= _maxTotal) break;
                int allowed = Volatile.Read(ref m.LastData) == 0 ? 1 : m.Cap;
                if (!m.Usable || Volatile.Read(ref m.Workers) >= allowed) continue;
                Interlocked.Increment(ref m.Workers);
                _workers.Add(Task.Run(() => WorkerAsync(m, ct)));
                running++;
                added = true;
            }
        }
    }

    async Task WorkerAsync(Mirror m, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        int failures = 0, busy = 0; // errors in a row; "busy" answers in a row, counted apart
        double rate = 0; // bytes per second this connection made on its last piece of work
        bool counted = true; // still in m.Workers
        try
        {
            while (m.Usable && !ct.IsCancellationRequested)
            {
                if (TryLeave(m))
                {
                    counted = false;
                    return;
                }
                var segment = Take(rate);
                if (segment == null)
                {
                    if (IsComplete) return;
                    await Task.Delay(250, ct);
                    continue;
                }
                Exception? error = null;
                bool stay = true;
                try
                {
                    long from, started = Environment.TickCount64;
                    lock (_lock) from = segment.Pos;
                    stay = await FetchAsync(m, segment, buffer, ct);
                    long took = Environment.TickCount64 - started, wrote;
                    lock (_lock) wrote = segment.Pos - from;
                    if (wrote > 0 && took > 0) rate = wrote * 1000.0 / took;
                }
                catch (Exception e)
                {
                    error = e;
                }
                Release(segment); // what this connection didn't write goes back for others to take
                if (!stay)
                {
                    counted = false;
                    return;
                }
                if (error is WriteError write)
                {
                    lock (_lock) WriteFailure ??= (IOException)write.InnerException!;
                    _stop?.Cancel();
                    return;
                }
                if (error == null)
                {
                    failures = busy = 0;
                    continue;
                }
                if (ct.IsCancellationRequested) return;
                // "Busy" (429, 503) while the link's other connections get data: waited out for a second or two, the
                // connection keeping its place and the link its connections - not an error, the link works. Ending
                // it at once cost half the speed against a host allowing 4 connections at once, since the Tuner then
                // rests the link before trying more again (docs/speed.md). Still busy after BusyRetries tries in a
                // row: the host doesn't want this many, and the connection leaves as for any other refusal.
                var next = error is SourceError { Status: 429 or 503 } && OthersWork(m) && ++busy <= BusyRetries
                    ? await WaitBusyAsync(m, error, busy, ct)
                    : await RecoverAsync(m, error, ++failures, ct);
                if (next == Next.Left) counted = false;
                if (next != Next.Retry) return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (counted) Interlocked.Decrement(ref m.Workers);
        }
    }

    /// <summary>
    /// Leaves the link's connections (true) when it has more than it may have: then this connection ends.
    /// Counted down at once, so that of several connections asking at the same time only as many leave as needed.
    /// </summary>
    static bool TryLeave(Mirror m)
    {
        while (true)
        {
            int workers = Volatile.Read(ref m.Workers);
            if (workers <= Volatile.Read(ref m.Cap)) return false;
            if (Interlocked.CompareExchange(ref m.Workers, workers - 1, workers) == workers) return true;
        }
    }

    /// <summary>Whether the link's other connections get data: then a refusal is about this connection, not the link.</summary>
    static bool OthersWork(Mirror m) =>
        Volatile.Read(ref m.Workers) > 1 && Environment.TickCount64 - Volatile.Read(ref m.LastData) < 5000;

    static string Note(Mirror m, Exception error)
    {
        string message = error is OperationCanceledException ? "no data for 60 s" : Format.Short(error);
        m.LastError = message;
        Volatile.Write(ref m.LastErrorAt, Environment.TickCount64);
        return message;
    }

    static async Task<Next> WaitBusyAsync(Mirror m, Exception error, int busy, CancellationToken ct)
    {
        Note(m, error);
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(2, busy)), ct);
        return Next.Retry;
    }

    /// <summary>Decides what to do after a failed request: try again after a pause, or end this connection.</summary>
    async Task<Next> RecoverAsync(Mirror m, Exception error, int failures, CancellationToken ct)
    {
        string message = Note(m, error);

        var source = error as SourceError;
        if (source is { Fatal: true })
        {
            m.Problem = message;
            return Next.Stop;
        }
        if (source is { Status: > 0 })
        {
            if (OthersWork(m))
            {
                // The link works but doesn't allow this many connections: this one ends, and the link goes on with
                // the ones it has left. Counted down first, so no other connection leaves as well. Whether more pay
                // off again later, Tuner finds out; it waits longer each time a link refuses them.
                int left = Interlocked.Decrement(ref m.Workers);
                lock (m) m.Cap = Math.Max(1, Math.Min(m.Cap, left));
                return Next.Left;
            }
            // A link that never worked is dropped right away; one that did may just still count our
            // previous connection as open, so it gets retried like a network error.
            bool workedBefore = Volatile.Read(ref m.LastData) != 0;
            if (!workedBefore && source.Status is not (408 or 429 or 500 or 502 or 503 or 504))
            {
                m.Problem = message;
                return Next.Stop;
            }
        }
        int errors = Interlocked.Increment(ref m.Errors);
        int limit = mirrors.Count(x => x.Usable) > 1 ? 10 : 30;
        if (errors >= limit)
        {
            m.Problem = $"gave up after {errors} errors ({message})";
            return Next.Stop;
        }
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failures, 5))), ct);
        return Next.Retry;
    }

    /// <summary>Downloads into the segment until it is done or taken over (true), or this connection leaves the link (false).</summary>
    async Task<bool> FetchAsync(Mirror m, Segment segment, byte[] buffer, CancellationToken ct)
    {
        long from, to;
        lock (_lock) (from, to) = (segment.Pos, segment.End - 1);
        if (from > to) return true;

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, m.Url);
        request.Headers.Range = new RangeHeaderValue(from, to);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new SourceError(response.IsSuccessStatusCode ? "server ignored the range request"
                : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", (int)response.StatusCode);
        var range = response.Content.Headers.ContentRange;
        if (range is { HasLength: true } && range.Length != size)
            throw new SourceError($"the file on the server changed (now {Format.Bytes(range.Length!.Value)})", fatal: true);
        if (range?.From != from)
            throw new SourceError("server sent the wrong part of the file", fatal: true);

        await using var body = await response.Content.ReadAsStreamAsync(stall.Token);
        while (true)
        {
            if (TryLeave(m)) return false;
            long left;
            lock (_lock) left = segment.End - segment.Reserved;
            if (left <= 0) return true;
            stall.CancelAfter(StallTimeout);
            int read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), stall.Token);
            if (read == 0) throw new SourceError("connection closed early");
            long at;
            int take;
            lock (_lock)
            {
                at = segment.Reserved;
                take = (int)Math.Min(read, segment.End - at);
                segment.Reserved = at + take;
            }
            if (take > 0)
            {
                try
                {
                    await RandomAccess.WriteAsync(file, buffer.AsMemory(0, take), at, ct);
                }
                catch (IOException e)
                {
                    throw new WriteError(e);
                }
                lock (_lock)
                {
                    segment.Pos = at + take;
                    segment.Mark(Environment.TickCount64);
                }
                m.GotData(take);
            }
            if (take < read) return true; // another connection took over the rest
        }
    }

    /// <summary>
    /// Work for a connection that goes at <paramref name="rate"/> bytes per second: a free range, half of the
    /// biggest unfinished segment, or - at the very end - the rest of one that lags far behind.
    /// </summary>
    Segment? Take(double rate)
    {
        lock (_lock)
        {
            Segment segment;
            var biggest = _active.MaxBy(s => s.End - s.Reserved);
            if (_free.Count > 0)
            {
                var (start, end) = _free[0];
                _free.RemoveAt(0);
                segment = new Segment(start, end);
            }
            else if (biggest != null && biggest.End - biggest.Reserved >= 2 * MinSplit)
            {
                // Split the unfinished segment with the most bytes left.
                long middle = biggest.Reserved + (biggest.End - biggest.Reserved) / 2;
                segment = new Segment(middle, biggest.End);
                biggest.End = middle;
            }
            else if (Lagging(rate) is { } slow)
            {
                // The end of the download, with nothing left to split: a connection that would still take a while
                // for what it has left - a slow server, or one that only trickles - hands the rest over. Its bytes
                // in flight are still its own; it stops at its next read (take < read in FetchAsync).
                segment = new Segment(slow.Reserved, slow.End);
                slow.End = slow.Reserved;
            }
            else
                return null;
            _active.Add(segment);
            return segment;
        }
    }

    /// <summary>
    /// The segment a connection going at <paramref name="mine"/> would finish far sooner: one that has run
    /// for a few seconds (so a connection still getting up to speed isn't judged), would still need more than
    /// <see cref="EndgameWait"/> at its speed, and goes at under a third of <paramref name="mine"/>. Its speed is
    /// the lower of its average and that of its last seconds: a link that starts fast and then slows down to a
    /// trickle keeps a high average for long, and its rest was taken over 4 times later (docs/speed.md).
    /// The margin keeps two connections from handing a part back and forth, since each hand-over starts a new
    /// request. Null if there is none, also while the connection's own speed isn't known yet.
    /// </summary>
    Segment? Lagging(double mine)
    {
        long now = Environment.TickCount64;
        Segment? pick = null;
        double longest = EndgameWait.TotalSeconds;
        foreach (var s in _active)
        {
            long left = s.End - s.Reserved;
            double seconds = (now - s.Since) / 1000.0;
            if (left <= 0 || seconds < 4) continue;
            double speed = (s.Pos - s.Start) / seconds;
            if (now > s.EarlierTime) speed = Math.Min(speed, (s.Pos - s.EarlierPos) * 1000.0 / (now - s.EarlierTime));
            double eta = speed > 0 ? left / speed : double.PositiveInfinity;
            if (eta > longest && mine > 3 * speed) (pick, longest) = (s, eta);
        }
        return pick;
    }

    void Release(Segment segment)
    {
        lock (_lock)
        {
            _active.Remove(segment);
            if (segment.Pos < segment.End)
            {
                _free.Add((segment.Pos, segment.End));
                _free.Sort();
            }
        }
    }
}
