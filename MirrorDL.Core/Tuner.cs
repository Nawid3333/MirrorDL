// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

namespace MirrorDL;

/// <summary>
/// Decides how many connections each link gets, so nobody has to guess. Every link starts with a few; then,
/// one link at a time, more are added and kept only if the whole download got faster by at least half of
/// what they should bring - and then only as many as the gain is worth. So it stops where a server, the
/// internet line or the disk is the limit - more connections there would only load the servers and risk
/// being refused, not speed anything up - and it doesn't count speed one link takes from another as a gain.
/// A link whose connections all bring their full share gets twice as many next, so a fast download is soon
/// at full speed; after a try that brought less, the steps are smaller.
/// </summary>
sealed class Tuner
{
    public const int StartPerLink = 4;
    /// <summary>
    /// The most connections one link gets. aria2 allows no more either: servers that see many more from one
    /// address often answer with errors or block it, which costs more than the extra connections bring.
    /// </summary>
    public const int MaxPerLink = 16;
    const long Settle = 1500;      // ms new connections get to come up to speed
    const long Window = 3000;      // ms the speed is measured over
    const long FirstRest = 30_000; // ms before a link that got no faster is tried again; doubles each time
    const long MaxRest = 300_000;

    readonly Mirror[] _mirrors;
    readonly int _maxTotal;
    readonly List<(long Time, long[] Bytes, int Workers)> _history = [];
    readonly Dictionary<Mirror, (long Until, long Wait)> _resting = [];
    readonly HashSet<Mirror> _smallSteps = [];
    Mirror? _link; // the link trying more connections, and what it had before
    int _before, _after, _remeasured;
    double _baseline, _perConnection;
    long _decideAt;

    public Tuner(IReadOnlyList<Mirror> mirrors, int maxPerLink, int maxTotal)
    {
        _mirrors = [.. mirrors];
        _maxTotal = maxTotal;
        foreach (var m in _mirrors)
            lock (m) (m.Ceiling, m.Cap) = (maxPerLink, Math.Min(StartPerLink, maxPerLink));
    }

    /// <summary>Call it about every half second. <paramref name="now"/> is <see cref="Environment.TickCount64"/>.</summary>
    public void Update(long now, long remaining)
    {
        _history.Add((now, [.. _mirrors.Select(m => Interlocked.Read(ref m.Bytes))], _mirrors.Sum(m => Volatile.Read(ref m.Workers))));
        // Keep the newest sample that is old enough for a settled measurement, and all after it.
        while (_history.Count > 1 && now - _history[1].Time >= Settle + Window) _history.RemoveAt(0);

        if (_link != null)
        {
            if (now >= _decideAt) Finish(now);
        }
        else
            TryMore(now, remaining);
    }

    /// <summary>
    /// Bytes per second over the last window, of all links or of one; null unless the number of connections
    /// stayed the same for the last <paramref name="steady"/> ms, since only then the speed tells what it brings.
    /// </summary>
    double? Speed(long now, long steady, int link = -1)
    {
        var last = _history[^1];
        if (now - _history[0].Time < steady || _history.Any(h => now - h.Time <= steady && h.Workers != last.Workers))
            return null;
        var first = _history.FindLast(h => now - h.Time >= Window);
        long bytes = link < 0 ? last.Bytes.Sum() - first.Bytes.Sum() : last.Bytes[link] - first.Bytes[link];
        return bytes * 1000.0 / (now - first.Time);
    }

    void TryMore(long now, long remaining)
    {
        if (Speed(now, Settle + Window) is not double speed || speed <= 0) return;
        if (remaining < speed * 10) return; // done in 10 s: little left for new connections to do

        Mirror? best = null;
        double bestPerConnection = 0;
        for (int i = 0; i < _mirrors.Length; i++)
        {
            var m = _mirrors[i];
            int workers = Volatile.Read(ref m.Workers);
            if (!m.Usable || workers == 0 || workers < m.Cap || m.Cap >= m.Ceiling) continue;
            if (_resting.TryGetValue(m, out var rest) && now < rest.Until) continue;
            if (now - Volatile.Read(ref m.LastErrorAt) < Settle + Window) continue; // retrying: its speed says little
            double perConnection = Speed(now, Settle + Window, i)!.Value / workers;
            if (perConnection > bestPerConnection) (best, bestPerConnection) = (m, perConnection);
        }
        if (best == null) return;

        lock (best)
        {
            int add = _smallSteps.Contains(best) ? Math.Max(2, best.Cap / 2) : best.Cap;
            add = Math.Min(add, Math.Min(best.Ceiling - best.Cap, _maxTotal - _history[^1].Workers));
            if (add <= 0) return;
            (_before, _after) = (best.Cap, best.Cap + add);
            best.Cap = _after;
        }
        (_link, _baseline, _perConnection, _decideAt, _remeasured) = (best, speed, bestPerConnection, now + Settle + Window, 0);
    }

    void Finish(long now)
    {
        var link = _link!;
        var measured = Speed(now, Window);
        // A connection that came or went meanwhile - an error on another link, say - spoils the measurement:
        // measure again rather than take it for "no faster". After three tries, give up the try.
        if (measured == null && _remeasured < 3 && link.Cap == _after)
        {
            _remeasured++;
            _decideAt = now + Window;
            return;
        }
        _link = null;
        int added = _after - _before;
        // How many connections' worth of speed the download gained.
        double worth = (measured - _baseline) / _perConnection ?? 0;
        lock (link)
        {
            // Cap is something else when the server refused connections meanwhile: then it stays as that left it.
            if (link.Cap == _after && worth >= added / 2.0)
            {
                link.Cap = _before + Math.Clamp((int)Math.Round(worth, MidpointRounding.AwayFromZero), 1, added);
                if (worth >= added * 0.8) _smallSteps.Remove(link);
                else _smallSteps.Add(link);
                _resting.Remove(link);
                return;
            }
            if (link.Cap == _after) link.Cap = _before;
        }
        _smallSteps.Add(link);
        long rest = _resting.TryGetValue(link, out var last) ? Math.Min(MaxRest, last.Wait * 2) : FirstRest;
        _resting[link] = (now + rest, rest);
    }
}
