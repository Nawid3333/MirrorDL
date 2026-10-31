// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace MirrorDL;

/// <summary>What the user asked for.</summary>
public sealed class Job
{
    public List<Uri> Urls { get; } = [];
    public List<string> Checksums { get; } = [];
    public string? PartFile { get; set; }
    public string? OutDir { get; set; }
    public int Connections { get; set; } = 16;
    public int PerServer { get; set; } = 8;
}

/// <summary>A problem the user can fix: shown as a plain message, without a stack trace.</summary>
public sealed class UserError(string message) : Exception(message);

public enum LogKind { Info, Ok, Warn, Error, Dim }

/// <summary>The steps of a download in order; it ends in one of the last four.</summary>
public enum Phase { Starting, Checking, Comparing, Downloading, Finishing, Verifying, Finished, Paused, Cancelled, Failed }

/// <summary>Where the download goes: the unfinished file and the final name.</summary>
public sealed record Target(string PartPath, string Name, string FinalDir)
{
    public string StatePath => PartPath + ".mirrordl";
    public string Aria2Path => PartPath + ".aria2";
    public string FinalPath => Path.Combine(FinalDir, Name);
}

public sealed record LinkStatus(Mirror Link, int Connections, double Speed, long Bytes, bool Retrying);

/// <summary>The numbers of a download at one moment.</summary>
public sealed record Status(Phase Phase, long? Size, long Have, double Speed, double AverageSpeed, TimeSpan Elapsed,
    IReadOnlyList<LinkStatus> Links, IReadOnlyList<(long Start, long End)> Missing, double VerifyPercent)
{
    public int Connections => Links.Sum(l => l.Connections);
    public double Percent => Size is > 0 ? Have * 100.0 / Size.Value : Phase == Phase.Finished ? 100 : 0;
    public double? SecondsLeft => Size is long size && Speed > 0 ? (size - Have) / Speed : null;
}

/// <summary>One download: checks the links, then downloads the file from all of them at once.</summary>
public sealed class Session
{
    const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0";
    /// <summary>Firefox may not have written the end of its .part file to disk yet, so that much is fetched again.</summary>
    const long TailRedo = 1 << 20;
    const long SaveIntervalMs = 10_000;

    readonly Job _job;
    readonly List<Mirror> _mirrors;
    readonly Dictionary<Mirror, SpeedMeter> _meters;
    readonly SpeedMeter _speed = new();
    readonly Stopwatch _watch = new();
    volatile Phase _phase;
    volatile Engine? _engine;
    volatile Mirror? _single;
    volatile List<(long Start, long End)>? _missing;
    bool _firefoxPrefix; // the unfinished file is Firefox's, with no progress file: only its end can be damaged
    long _verifyDone, _verifyTotal;

    public Session(Job job)
    {
        _job = job;
        _mirrors = job.Urls.Select((url, i) => new Mirror(i + 1, url)).ToList();
        _meters = _mirrors.ToDictionary(m => m, _ => new SpeedMeter());
    }

    public Job Job => _job;
    public IReadOnlyList<Mirror> Mirrors => _mirrors;
    public Phase Phase => _phase;
    public Target? Target { get; private set; }
    public long? Size { get; private set; }
    public string? SavedPath { get; private set; }
    public string? ErrorMessage { get; private set; }
    /// <summary>When paused or stopped: what that means for the unfinished file, and what to do next.</summary>
    public string? Note { get; private set; }

    /// <summary>A message for the user. Raised on a background thread.</summary>
    public event Action<string, LogKind>? Log;
    /// <summary>Raised on a background thread, before any message of the new phase.</summary>
    public event Action<Phase>? PhaseChanged;
    /// <summary>The links were checked; <see cref="Mirror.Problem"/> tells which ones are used.</summary>
    public event Action? LinksChecked;
    /// <summary>Asks the user a yes/no question.</summary>
    public Func<string, Task<bool>> ConfirmAsync { get; set; } = _ => Task.FromResult(false);

    /// <summary>Returns 0 when done, 1 on an error, 2 when the checksum doesn't match, 130 when paused or cancelled.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        try
        {
            return await RunCoreAsync(ct);
        }
        catch (UserError e)
        {
            SetPhase(Phase.Failed);
            ErrorMessage = e.Message;
            Write(e.Message, LogKind.Error);
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file MirrorDL needs could not be read, written or moved: in use, no access, drive gone.
            SetPhase(Phase.Failed);
            ErrorMessage = "A file could not be used: " + e.Message;
            Write(ErrorMessage, LogKind.Error);
            return 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            bool downloaded = _phase is Phase.Verifying or Phase.Finishing;
            SetPhase(Phase.Cancelled);
            Note = downloaded ? "The download itself is complete: start again with the same links to check and save it." : null;
            Write(downloaded ? "Stopped. " + Note : "Cancelled.", LogKind.Warn);
            return 130;
        }
    }

    /// <summary>The current numbers. Call it from one place only, since it also measures the speed.</summary>
    public Status GetStatus()
    {
        long? size = Size;
        IReadOnlyList<(long Start, long End)> missing;
        long have;
        if (_single is { } single)
        {
            have = Interlocked.Read(ref single.Bytes);
            missing = size is long total && have < total ? [(have, total)] : [];
        }
        else
        {
            missing = _engine?.Remaining() ?? _missing ?? (IReadOnlyList<(long, long)>)[];
            have = size is long total ? total - Ranges.Sum(missing) : 0;
            if (_engine == null && _missing == null) have = 0;
        }

        long now = Environment.TickCount64;
        var links = _mirrors.Select(m =>
        {
            long bytes = Interlocked.Read(ref m.Bytes);
            return new LinkStatus(m, Volatile.Read(ref m.Workers), _meters[m].Update(bytes), bytes,
                m.Usable && now - Volatile.Read(ref m.LastErrorAt) < 10_000);
        }).ToList();
        long received = links.Sum(l => l.Bytes);
        double seconds = _watch.Elapsed.TotalSeconds;
        long verifyTotal = Interlocked.Read(ref _verifyTotal);
        double verify = verifyTotal > 0 ? Interlocked.Read(ref _verifyDone) * 100.0 / verifyTotal : 0;
        return new Status(_phase, size, have, _speed.Update(received), seconds > 0 ? received / seconds : 0,
            _watch.Elapsed, links, missing, verify);
    }

    async Task<int> RunCoreAsync(CancellationToken ct)
    {
        using var http = CreateClient();
        SetPhase(Phase.Checking);
        Write($"MirrorDL {AppInfo.Version}", LogKind.Dim); // so a copied log says which version it came from
        Write($"Checking {Format.Plural(_mirrors.Count, "link")}...");
        await Task.WhenAll(_mirrors.Select(m => m.InspectAsync(http, ct)));
        if (!_mirrors.Any(m => m.Usable))
        {
            LinksChecked?.Invoke();
            throw new UserError("None of the links work.");
        }

        // A link with another size than most of the others leads to a different file.
        var bySize = _mirrors.Where(m => m.Usable).GroupBy(m => m.Size)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Min(m => m.Index)).ToList();
        foreach (var m in bySize.Skip(1).SelectMany(g => g))
            m.Problem = $"different size ({(m.Size is long s ? Format.Bytes(s) : "unknown")}), skipped";
        var group = bySize[0].ToList();
        bool split = group.Any(m => m.SupportsRanges);
        if (split)
            foreach (var m in group.Where(m => !m.SupportsRanges))
                m.Problem = "can't send parts of the file, skipped";

        Target = FindTarget(group);
        Size = group[0].Size;
        bool complete;
        if (Size == 0)
        {
            // An empty file: nothing to download, but it is saved and checked like any other.
            LinksChecked?.Invoke();
            Directory.CreateDirectory(Path.GetDirectoryName(Target.PartPath)!);
            File.WriteAllBytes(Target.PartPath, []);
            File.Delete(Target.StatePath);
            complete = true;
        }
        else
        {
            complete = split
                ? await MultiSourceAsync(http, group, ct)
                : await SingleSourceAsync(http, group[0], ct);
        }
        if (!complete)
        {
            if (split)
            {
                SetPhase(Phase.Paused);
                Note = "The progress is saved: start again with the same links to continue.";
            }
            else
            {
                SetPhase(Phase.Cancelled);
                Note = $"The unfinished file is kept ({Target.PartPath}), but this server can't continue a download, so the next start begins from the beginning.";
            }
            Write((split ? "Paused. " : "Stopped. ") + Note, LogKind.Warn);
            return 130;
        }

        SetPhase(Phase.Finishing);
        long received = _mirrors.Sum(m => m.Bytes);
        double seconds = Math.Max(0.001, _watch.Elapsed.TotalSeconds);
        Write($"Downloaded {Format.Bytes(received)} in {Format.Duration(seconds)} ({Format.Speed(received / seconds)} on average).");
        bool ok = await FinishAsync(ct);
        SetPhase(Phase.Finished);
        return ok ? 0 : 2;
    }

    /// <summary>Picks the file to write into: the given .part file, an unfinished download found in the folder, or a new one.</summary>
    Target FindTarget(List<Mirror> group)
    {
        var names = group.Select(m => m.FileName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string serverName = group.FirstOrDefault(m => m.NameFromHeader)?.FileName ?? names.FirstOrDefault() ?? "download";
        if (_job.PartFile != null)
            return new Target(_job.PartFile, PartFinder.FinalName(_job.PartFile, names), _job.OutDir ?? Path.GetDirectoryName(_job.PartFile)!);

        string dir = _job.OutDir ?? Native.DownloadsFolder();
        var found = PartFinder.Find(dir, names);
        foreach (string other in found.Skip(1))
            Write($"Also found {Path.GetFileName(other)} ({Format.Bytes(new FileInfo(other) is { Exists: true } info ? info.Length : 0)}), not used.", LogKind.Dim);
        return found.Count > 0
            ? new Target(found[0], PartFinder.FinalName(found[0], names), dir)
            : new Target(Path.Combine(dir, serverName + ".part"), serverName, dir);
    }

    async Task<bool> MultiSourceAsync(HttpClient http, List<Mirror> group, CancellationToken ct)
    {
        var target = Target!;
        long size = Size!.Value;
        var missing = ExistingProgress(target, size);
        _missing = missing;
        var candidates = group.Where(m => m.Usable).ToList();
        if (candidates.Count > 1 || Ranges.Sum(missing) < size)
        {
            SetPhase(Phase.Comparing);
            missing = await CompareContentAsync(http, candidates, target.PartPath, size, missing, ct);
            _missing = missing;
        }
        LinksChecked?.Invoke();

        var sources = candidates.Where(m => m.Usable).ToList();
        if (sources.Count == 0) throw new UserError("None of the links can be used.");
        CheckDiskSpace(target.PartPath, size, Ranges.Sum(missing));
        Write($"File:      {target.Name} ({Format.Bytes(size)})");
        Write($"Save to:   {target.FinalDir}");
        foreach (string hex in _job.Checksums)
            Write($"Checksum:  {Checksum.Algorithm(hex)!.Value.Name} {hex}");
        Write($"Using {Format.Plural(sources.Count, "link")} with up to " +
              $"{Math.Min(_job.Connections, sources.Count * _job.PerServer)} connections.");

        PrepareFile(target.PartPath);
        new PartState(size, missing).Save(target.StatePath); // before any data is written
        using var file = File.OpenHandle(target.PartPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
        var engine = new Engine(http, file, size, missing, sources, _job.Connections, _job.PerServer);
        _engine = engine;
        Task saving = Task.CompletedTask;
        long nextSave = Environment.TickCount64 + SaveIntervalMs;

        void Save()
        {
            var stillMissing = engine.Remaining(); // only bytes that were already written...
            Native.Flush(file);                    // ...and are now on the disk, not just in the cache
            new PartState(size, stillMissing).Save(target.StatePath);
        }

        SetPhase(Phase.Downloading);
        _watch.Start();
        bool complete;
        try
        {
            complete = await engine.RunAsync(() =>
            {
                if (Environment.TickCount64 >= nextSave && saving.IsCompleted)
                {
                    nextSave = Environment.TickCount64 + SaveIntervalMs;
                    saving = Task.Run(Save);
                }
            }, ct);
        }
        finally
        {
            _watch.Stop();
            try
            {
                await saving.ContinueWith(_ => { }, TaskScheduler.Default); // a failed earlier save doesn't matter now
                Save();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Write("Could not save the progress: " + e.Message, LogKind.Warn);
            }
        }

        if (engine.WriteFailure is { } failure)
        {
            SetPhase(Phase.Failed);
            throw new UserError($"Could not write to {target.PartPath}: {failure.Message.TrimEnd('.')}. " +
                                "The progress up to the last save is kept - make room on the drive (or plug it back in) and start again.");
        }
        if (!complete && !ct.IsCancellationRequested)
        {
            SetPhase(Phase.Failed);
            LinksChecked?.Invoke();
            throw new UserError("All links failed. The progress is saved - start again (with new links if they expired) to continue.");
        }
        return complete;
    }

    /// <summary>The ranges still missing, from mirrordl's or aria2's progress file, or from a Firefox .part file.</summary>
    List<(long Start, long End)> ExistingProgress(Target target, long size)
    {
        string partName = Path.GetFileName(target.PartPath);
        if (!File.Exists(target.PartPath))
        {
            File.Delete(target.StatePath); // left over from an unfinished file that is gone
            return [(0, size)];
        }
        long length = new FileInfo(target.PartPath).Length;
        if (length > size)
            throw new UserError($"{partName} ({Format.Bytes(length)}) is bigger than the file behind the links ({Format.Bytes(size)}), so it is a different file.");

        EnsureAria2Stopped(target.Aria2Path);
        var (state, from) = PartState.Load(target.StatePath) is { } own ? (own, "mirrordl")
            : Aria2Control.Load(target.Aria2Path) is { } aria2 ? (aria2, "aria2")
            : (null, null);
        if (state != null)
        {
            if (state.Size != size)
                throw new UserError($"{partName} was started for a file of {Format.Bytes(state.Size)}, but the links have {Format.Bytes(size)}. " +
                                    "The file has changed - delete the unfinished file to start over.");
            // Whatever the progress file says, nothing past the end of the file can be there: the file was
            // cut short or replaced by an older copy since.
            var missing = length < size ? Ranges.Normalize(state.Missing.Append((length, size))) : state.Missing;
            Write($"Found {partName} ({from} progress): {Format.Bytes(size - Ranges.Sum(missing))} " +
                  $"of {Format.Bytes(size)} already downloaded.");
            return missing;
        }

        // No progress file: Firefox (or another program) downloaded the beginning of the file in one piece.
        long keep = Math.Max(0, length - TailRedo);
        _firefoxPrefix = PartFinder.IsFirefoxPart(target.PartPath);
        Write($"Found {partName}: it holds the first {Format.Bytes(length)} of {Format.Bytes(size)}.");
        return [(keep, size)];
    }

    static void EnsureAria2Stopped(string controlPath)
    {
        if (File.Exists(controlPath)
            && DateTime.UtcNow - File.GetLastWriteTimeUtc(controlPath) < TimeSpan.FromMinutes(2)
            && Process.GetProcessesByName("aria2c").Length > 0)
            throw new UserError("aria2 is running, and this download's .aria2 file changed in the last 2 minutes. If aria2 is still " +
                                "working on it, stop aria2 first (Ctrl+C in its window); if not, try again in 2 minutes.");
    }

    /// <summary>
    /// Compares a few pieces of every link, and of what is already downloaded, so that only links with the
    /// same file are used and a different file is never continued. Returns the ranges still missing: as
    /// given, or - for a Firefox file whose end a crash left empty - with that end added.
    /// </summary>
    async Task<List<(long Start, long End)>> CompareContentAsync(HttpClient http, List<Mirror> mirrors, string partPath,
        long size, List<(long Start, long End)> missing, CancellationToken ct)
    {
        var have = Ranges.Invert(missing, size);
        int length = (int)Math.Min(Mirror.SampleLength, size);
        var offsets = SampleOffsets(size, length, have);
        Write($"Comparing {offsets.Count} pieces of the file across {Format.Plural(mirrors.Count, "link")}...");
        await Task.WhenAll(mirrors.Select(m => m.SampleAsync(http, offsets, length, ct)));

        // The content most links agree on wins.
        var byContent = mirrors.Where(m => m.Usable).GroupBy(m => m.Fingerprint)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Min(m => m.Index)).ToList();
        if (byContent.Count == 0)
        {
            LinksChecked?.Invoke();
            throw new UserError("Could not read from any of the links.");
        }
        foreach (var m in byContent.Skip(1).SelectMany(g => g))
            m.Problem = "different content than the other links, skipped";
        var source = byContent[0].First();
        var reference = source.Samples!;

        if (have.Count == 0) return missing;
        using var part = File.OpenHandle(partPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[length];
        int compared = 0;
        long lastGood = -1;
        for (int i = 0; i < offsets.Count; i++)
        {
            long offset = offsets[i];
            if (!have.Any(r => r.Start <= offset && offset + length <= r.End)) continue;
            ReadAt(part, buffer, offset);
            if (SHA256.HashData(buffer).AsSpan().SequenceEqual(reference[i]))
            {
                compared++;
                lastGood = offset;
                continue;
            }
            // Firefox writes from the start to the end; after a crash its file can end in bytes that never reached
            // the disk and read as zeros. Then the part before them is good, and only the rest is downloaded again.
            if (_firefoxPrefix && have is [(0, var kept)])
            {
                long good = await GoodPrefixAsync(http, source, part, Path.GetFileName(partPath), lastGood, offset, length, ct);
                Write($"The last {Format.Bytes(kept - good)} of {Path.GetFileName(partPath)} never reached the disk " +
                      "(Firefox stopped before it could write them); they are downloaded again.", LogKind.Warn);
                return [(good, size)];
            }
            throw new UserError($"{Path.GetFileName(partPath)} is not the same file as the links (it differs at {Format.Bytes(offset)}). " +
                                "It is probably another file or version - move or delete it to download from scratch.");
        }
        if (compared > 0) Write($"The unfinished file matches the links ({Format.Plural(compared, "piece")} compared).", LogKind.Ok);
        return missing;
    }

    /// <summary>
    /// Where the good beginning of a Firefox file ends. <paramref name="bad"/> is a piece that differs from the
    /// links, <paramref name="good"/> one before it that matches (-1 if none). Each piece looked at is compared
    /// byte by byte: it must match, or match up to a point and be zeros from there to its end - the bytes that
    /// never reached the disk. Anything else means another file, and is refused. Between the two, a binary
    /// search: about 20 pieces of 64 KB even for 100 GB.
    /// </summary>
    static async Task<long> GoodPrefixAsync(HttpClient http, Mirror source, SafeFileHandle part, string partName, long good,
        long bad, int length, CancellationToken ct)
    {
        var mine = new byte[length];

        // -1: the piece matches; otherwise where its zeros begin.
        async Task<int> CheckAsync(long offset)
        {
            byte[] theirs = await source.ReadRangeAsync(http, offset, length, ct);
            ReadAt(part, mine, offset);
            int same = mine.AsSpan().CommonPrefixLength(theirs);
            if (same == length) return -1;
            if (mine.AsSpan(same).IndexOfAnyExcept((byte)0) < 0) return same;
            throw new UserError($"{partName} is not the same file as the links (it differs at {Format.Bytes(offset + same)}). " +
                                "It is probably another file or version - move or delete it to download from scratch.");
        }

        int zeros = await CheckAsync(bad);
        if (zeros > 0) return bad + zeros;   // the zeros begin inside this very piece
        if (good < 0) return 0;              // not even the first piece reached the disk
        while (bad - good > length)
        {
            long middle = good + (bad - good) / 2;
            zeros = await CheckAsync(middle);
            if (zeros < 0) good = middle;
            else if (zeros > 0) return middle + zeros;
            else bad = middle;
        }
        return bad; // everything before it matched; it is zeros from its first byte
    }

    /// <summary>Reads <paramref name="buffer"/>.Length bytes at <paramref name="offset"/>; what lies past the end of the file reads as zeros.</summary>
    static void ReadAt(SafeFileHandle file, byte[] buffer, long offset)
    {
        Array.Clear(buffer);
        int read = 0, n;
        while (read < buffer.Length && (n = RandomAccess.Read(file, buffer.AsSpan(read), offset + read)) > 0) read += n;
    }

    /// <summary>Where to compare: start, a third, two thirds, end, and inside what is already downloaded.</summary>
    static List<long> SampleOffsets(long size, int length, List<(long Start, long End)> have)
    {
        var offsets = new SortedSet<long>();
        void Add(long offset) => offsets.Add(Math.Clamp(offset, 0, size - length));
        Add(0);
        Add(size / 3);
        Add(size / 3 * 2);
        Add(size - length);
        foreach (var (start, end) in have.Where(r => r.End - r.Start >= length).OrderByDescending(r => r.End - r.Start).Take(2))
        {
            Add(start + (end - start - length) / 2);
            Add(end - length);
        }
        return [.. offsets];
    }

    /// <summary>For servers that can only send the whole file: one connection, no resuming.</summary>
    async Task<bool> SingleSourceAsync(HttpClient http, Mirror m, CancellationToken ct)
    {
        var target = Target!;
        long? size = Size;
        LinksChecked?.Invoke();
        Write($"{m.Url.Authority} can't send parts of the file: downloading over one connection, and an interrupted download can't be continued.", LogKind.Warn);
        if (File.Exists(target.PartPath) && !await ConfirmAsync(
                $"{Path.GetFileName(target.PartPath)} is already there ({Format.Bytes(new FileInfo(target.PartPath).Length)}), but " +
                $"{m.Url.Authority} can't continue a download. Download the whole file again and replace it?"))
            throw new UserError("Nothing downloaded; the unfinished file is left as it was.");
        if (size is long known) CheckDiskSpace(target.PartPath, known, known);
        Directory.CreateDirectory(Path.GetDirectoryName(target.PartPath)!);
        File.Delete(target.StatePath);

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(Engine.StallTimeout);
        _single = m;
        SetPhase(Phase.Downloading);
        _watch.Start();
        try
        {
            using var response = await http.GetAsync(m.Url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            if (!response.IsSuccessStatusCode)
                throw new UserError($"{m.Label}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            await using var body = await response.Content.ReadAsStreamAsync(stall.Token);
            await using var output = new FileStream(target.PartPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, FileOptions.Asynchronous);
            var buffer = new byte[256 * 1024];
            while (true)
            {
                stall.CancelAfter(Engine.StallTimeout);
                int read = await body.ReadAsync(buffer, stall.Token);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                m.GotData(read);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            throw new UserError("The server stopped sending data.");
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            throw new UserError("Download failed: " + Format.Short(e));
        }
        finally
        {
            _watch.Stop();
        }
        if (size is long expected && m.Bytes != expected)
            throw new UserError("The connection closed before the whole file arrived.");
        return true;
    }

    async Task<bool> FinishAsync(CancellationToken ct)
    {
        var target = Target!;
        using (var stream = new FileStream(target.PartPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            Native.SetSparse(stream.SafeFileHandle, false);
            if (Size is long expected && stream.Length != expected)
                throw new UserError($"The finished file has {stream.Length} bytes instead of {expected}. Delete {target.PartPath} and try again.");
        }

        bool ok = true;
        if (_job.Checksums.Count > 0)
        {
            SetPhase(Phase.Verifying);
            var results = await Checksum.ComputeAsync(target.PartPath, _job.Checksums, (done, total) =>
            {
                Interlocked.Exchange(ref _verifyTotal, total);
                Interlocked.Exchange(ref _verifyDone, done);
            }, ct);
            SetPhase(Phase.Finishing);
            foreach (var (algorithm, expected, actual) in results)
            {
                if (actual == expected)
                    Write($"{algorithm} matches: {actual}", LogKind.Ok);
                else
                {
                    ok = false;
                    Write($"{algorithm} does NOT match!\n  expected {expected}\n  got      {actual}", LogKind.Error);
                }
            }
        }
        File.Delete(target.StatePath);
        File.Delete(target.Aria2Path);

        string path = target.FinalPath;
        if (!string.Equals(Path.GetFullPath(target.PartPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(target.FinalDir);
            path = FreePath(path);
            File.Move(target.PartPath, path);
        }
        SavedPath = path;
        if (ok) Write($"Saved: {path}", LogKind.Ok);
        else Write($"Saved, but the checksum does NOT match, so the file is probably damaged: {path}", LogKind.Warn);
        if (_job.Checksums.Count == 0)
            Write("Tip: paste the checksum from the download page together with the links, and the whole file gets checked.", LogKind.Dim);
        return ok;
    }

    /// <summary>The path itself, or "name (1).ext" etc. if it's taken. Firefox's empty placeholder is replaced.</summary>
    static string FreePath(string path)
    {
        if (new FileInfo(path) is { Exists: true, Length: 0 }) File.Delete(path);
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 1; File.Exists(path) || Directory.Exists(path); i++)
            path = Path.Combine(dir, $"{stem} ({i}){ext}");
        return path;
    }

    static void PrepareFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            Native.SetSparse(stream.SafeFileHandle, true);
        }
        catch (IOException e) when ((e.HResult & 0xFFFF) is 32 or 33) // sharing or lock violation
        {
            throw new UserError($"{Path.GetFileName(path)} is in use by another program (Firefox? aria2? another MirrorDL?). " +
                                "Close it and try again - but don't cancel the download in Firefox, that deletes the .part file.");
        }
    }

    static void CheckDiskSpace(string path, long size, long need)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
            if (drive.DriveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && size >= 4L << 30)
                throw new UserError($"{drive.Name} is formatted as FAT32, which can't hold files of 4 GB or more. Save it on another drive.");
            if (drive.AvailableFreeSpace < need)
                throw new UserError($"Not enough space on {drive.Name}: need {Format.Bytes(need)}, free {Format.Bytes(drive.AvailableFreeSpace)}.");
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Network drives and the like: can't tell, just try.
        }
    }

    void SetPhase(Phase phase)
    {
        if (_phase == phase) return;
        _phase = phase;
        PhaseChanged?.Invoke(phase);
    }

    void Write(string text, LogKind kind = LogKind.Info) => Log?.Invoke(text, kind);

    static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None, // keep the bytes exactly as the server has them
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxAutomaticRedirections = 20,
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return http;
    }
}
