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
    /// <summary>Unfinished downloads of the file the user gave, under any name: checked against the links and put together.</summary>
    public List<string> PartFiles { get; } = [];
    public string? OutDir { get; set; }
    /// <summary>Limits on the connections, in total and per link; null leaves it all to <see cref="Tuner"/>.</summary>
    public int? Connections { get; set; }
    public int? PerServer { get; set; }
}

/// <summary>A problem the user can fix: shown as a plain message, without a stack trace.</summary>
public sealed class UserError(string message) : Exception(message);

public enum LogKind { Info, Ok, Warn, Error, Dim }

/// <summary>The steps of a download in order; it ends in one of the last four.</summary>
public enum Phase { Starting, Checking, Comparing, Copying, Downloading, Finishing, Verifying, Finished, Paused, Cancelled, Failed }

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
    long _verifyDone, _verifyTotal;
    List<string> _names = [];          // the file names the links give
    string _serverName = "download";   // the name a new download gets
    /// <summary>The name the links give, if any: without one, an unfinished file keeps its own.</summary>
    string? LinkName => _serverName == "download" ? null : _serverName;
    List<string> _found = [];          // unfinished files of this download: the given ones first
    readonly List<Unfinished> _spare = []; // other unfinished files of this download, not needed once it is done
    readonly List<string> _skipped = [];
    string? _refusal; // why an unfinished file with the download's name can't be used, if none can
    (byte[][] Reference, List<long> Offsets, int Length)? _pieces; // pieces compared before the download

    /// <summary>
    /// An unfinished file of this download, what it holds, its size and time when it was read, and whether pieces
    /// of it were found to match the links.
    /// </summary>
    sealed record Unfinished(string FilePath, List<(long Start, long End)> Have, bool OnePiece, bool Firefox,
        long Length = 0, DateTime Written = default, bool Matched = false)
    {
        public string Name => Path.GetFileName(FilePath);
        public long Bytes => Ranges.Sum(Have);
    }

    /// <summary>An unfinished file differs from the links at <see cref="At"/>, and not only by zeros a crash left.</summary>
    sealed class DifferentFile(long at) : Exception
    {
        public long At { get; } = at;
    }

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
            // An empty file: nothing to download, but it is saved and checked like any other - as a file of its
            // own, never written over an unfinished file that was found or given.
            LinksChecked?.Invoke();
            Directory.CreateDirectory(Target.FinalDir);
            Target = Target with { PartPath = FreePath(Path.Combine(Target.FinalDir, _serverName + ".part")), Name = _serverName };
            File.WriteAllBytes(Target.PartPath, []);
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

    /// <summary>
    /// Finds the unfinished files of this download - the given ones, then those in their folders and the save
    /// folder - and picks the file to write into for now: the first of them, or a new one.
    /// </summary>
    Target FindTarget(List<Mirror> group)
    {
        _names = group.Select(m => m.FileName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _serverName = group.FirstOrDefault(m => m.NameFromHeader)?.FileName ?? _names.FirstOrDefault() ?? "download";
        var given = _job.PartFiles.Select(Path.GetFullPath).ToList();
        string dir = _job.OutDir ?? (given.Count > 0 ? Path.GetDirectoryName(given[0])! : Native.DownloadsFolder());
        var folders = given.Select(p => Path.GetDirectoryName(p)!).Append(dir).Distinct(StringComparer.OrdinalIgnoreCase);
        // Files with the download's own name before numbered copies; within them, the most promising first.
        var found = folders.SelectMany(f => PartFinder.Find(f, _names)).OrderByDescending(p => PartFinder.HasName(p, _names));
        _found = given.Concat(found).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // A numbered copy may be another download of the same name: it is only written into once its content
        // matched (see MultiSourceAsync), never by a server that can only send the whole file.
        string? first = _found.FirstOrDefault(p => given.Contains(p, StringComparer.OrdinalIgnoreCase) || PartFinder.HasName(p, _names));
        return first != null
            ? new Target(first, PartFinder.FinalName(first, _names, LinkName), dir)
            : new Target(Path.Combine(dir, _serverName + ".part"), _serverName, dir);
    }

    async Task<bool> MultiSourceAsync(HttpClient http, List<Mirror> group, CancellationToken ct)
    {
        long size = Size!.Value;
        var files = _found.Select(p => ReadUnfinished(p, size)).OfType<Unfinished>().ToList();
        _missing = files.Count > 0 ? Ranges.Invert(files[0].Have, size) : [(0, size)];
        var candidates = group.Where(m => m.Usable).ToList();
        if (candidates.Count > 1 || files.Any(f => f.Bytes > 0))
        {
            SetPhase(Phase.Comparing);
            files = await CompareContentAsync(http, candidates, files, size, ct);
        }
        if (files.Count == 0 && _refusal != null) throw new UserError(_refusal);
        foreach (string why in _skipped) Write(why + " It is not used, and stays as it is.", LogKind.Warn);

        // The file holding the most becomes the download; what the others hold beyond it is copied in.
        var main = files.MaxBy(f => f.Bytes);
        if (main != null && main.FilePath != Target!.PartPath)
            Target = Target with { PartPath = main.FilePath, Name = PartFinder.FinalName(main.FilePath, _names, LinkName) };
        else if (main == null && _found.Count > 0)
            Target = Target! with { PartPath = Path.Combine(Target.FinalDir, _serverName + ".part"), Name = _serverName };
        var target = Target!;
        var others = files.Where(f => f != main).ToList();
        _spare.AddRange(others);
        var missing = main != null ? Ranges.Invert(main.Have, size) : [(0, size)];
        _missing = missing;
        LinksChecked?.Invoke();

        var sources = candidates.Where(m => m.Usable).ToList();
        if (sources.Count == 0) throw new UserError("None of the links can be used.");
        CheckDiskSpace(target.PartPath, size, Ranges.Sum(missing));
        Write($"File:      {target.Name} ({Format.Bytes(size)})");
        Write($"Save to:   {target.FinalDir}");
        foreach (string hex in _job.Checksums)
            Write($"Checksum:  {Checksum.Algorithm(hex)!.Value.Name} {hex}");
        int most = Math.Min(_job.Connections ?? int.MaxValue, sources.Count * (_job.PerServer ?? Tuner.MaxPerLink));
        Write($"Using {Format.Plural(sources.Count, "link")}, with more connections as long as that makes it faster " +
              $"(at most {most}).");

        PrepareFile(target.PartPath);
        new PartState(size, missing).Save(target.StatePath); // before any data is written
        using var file = File.OpenHandle(target.PartPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
        missing = await CopyFromAsync(file, others, missing, size, target.StatePath, ct);
        if (ct.IsCancellationRequested) return false; // paused while copying: what was copied is saved
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

    /// <summary>
    /// Copies into the download what other unfinished files of it hold and it still lacks: copying on the disk is
    /// much faster than downloading. What was copied is saved as done, also when paused in between. Returns the
    /// ranges still missing.
    /// </summary>
    async Task<List<(long Start, long End)>> CopyFromAsync(SafeFileHandle file, List<Unfinished> others,
        List<(long Start, long End)> missing, long size, string statePath, CancellationToken ct)
    {
        var plan = new List<(Unfinished From, List<(long Start, long End)> Take)>();
        var left = missing;
        foreach (var other in others.OrderByDescending(o => Ranges.Sum(Ranges.Intersect(o.Have, missing))))
        {
            var take = Ranges.Intersect(other.Have, left);
            if (take.Count == 0)
                Write($"{other.Name} holds nothing that this download still lacks.", LogKind.Dim);
            else
            {
                plan.Add((other, take));
                left = Ranges.Subtract(left, take);
            }
        }
        if (plan.Count == 0) return missing;

        SetPhase(Phase.Copying);
        var copied = new List<(long Start, long End)>();
        var buffer = new byte[1 << 20];
        try
        {
            foreach (var (other, take) in plan)
            {
                SafeFileHandle source;
                try
                {
                    source = File.OpenHandle(other.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Write($"Could not open {other.Name} ({e.Message.TrimEnd('.')}); what it holds is downloaded instead.", LogKind.Warn);
                    continue;
                }
                using var opened = source;
                long before = Ranges.Sum(copied);
                string? failure = null;
                foreach (var (start, end) in take)
                {
                    for (long pos = start; pos < end && failure == null;)
                    {
                        int read;
                        try
                        {
                            read = await RandomAccess.ReadAsync(source, buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - pos)), pos, ct);
                            if (read == 0) failure = "it is shorter than its progress file says";
                        }
                        catch (IOException e)
                        {
                            read = 0;
                            failure = e.Message;
                        }
                        if (read == 0) break;
                        await RandomAccess.WriteAsync(file, buffer.AsMemory(0, read), pos, ct);
                        if (copied.Count > 0 && copied[^1].End == pos) copied[^1] = (copied[^1].Start, pos + read);
                        else copied.Add((pos, pos + read));
                        pos += read;
                        _missing = Ranges.Subtract(missing, copied);
                    }
                    if (failure != null) break;
                }
                long took = Ranges.Sum(copied) - before;
                if (failure != null)
                    Write($"Could not read all of {other.Name} ({failure}); took {Format.Bytes(took)} from it, the rest is downloaded.", LogKind.Warn);
                else
                    Write($"Took {Format.Bytes(took)} from {other.Name}.", LogKind.Ok);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            // Only what is on the disk counts as done.
            Native.Flush(file);
            missing = Ranges.Subtract(missing, copied);
            _missing = missing;
            try
            {
                new PartState(size, missing).Save(statePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The progress file keeps what it had before the copying, which is less but true.
                Write("Could not save the progress: " + e.Message, LogKind.Warn);
            }
        }
        return missing;
    }

    /// <summary>
    /// What an unfinished file holds, from mirrordl's or aria2's progress file or - written from the start in one
    /// piece, as browsers do - its beginning. Null when it can't be part of this download (see <see cref="Skip"/>).
    /// </summary>
    Unfinished? ReadUnfinished(string path, long size)
    {
        try
        {
            return ReadUnfinishedFile(path, size);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Skip(path, $"{Path.GetFileName(path)} can't be read ({e.Message.TrimEnd('.')}).");
            return null;
        }
    }

    Unfinished? ReadUnfinishedFile(string path, long size)
    {
        string name = Path.GetFileName(path);
        if (!File.Exists(path))
        {
            File.Delete(path + ".mirrordl"); // left over from an unfinished file that is gone
            return new Unfinished(path, [], false, false);
        }
        var info = new FileInfo(path);
        long length = info.Length;
        if (length > size)
        {
            Skip(path, $"{name} ({Format.Bytes(length)}) is bigger than the file behind the links ({Format.Bytes(size)}), so it is a different file.");
            return null;
        }

        PartState? state;
        string? from;
        try
        {
            EnsureAria2Stopped(path + ".aria2");
            (state, from) = PartState.Load(path + ".mirrordl") is { } own ? (own, "mirrordl")
                : Aria2Control.Load(path + ".aria2") is { } aria2 ? (aria2, "aria2")
                : (null, null);
        }
        catch (UserError e) // aria2 is still at it, or a damaged progress file
        {
            Skip(path, e.Message);
            return null;
        }
        if (state != null)
        {
            if (state.Size != size)
            {
                Skip(path, $"{name} was started for a file of {Format.Bytes(state.Size)}, but the links have {Format.Bytes(size)}.",
                     "The file has changed - delete the unfinished file to start over.");
                return null;
            }
            // Whatever the progress file says, nothing past the end of the file can be there: the file was
            // cut short or replaced by an older copy since.
            var missing = length < size ? Ranges.Normalize(state.Missing.Append((length, size))) : state.Missing;
            Write($"Found {name} ({from} progress): {Format.Bytes(size - Ranges.Sum(missing))} " +
                  $"of {Format.Bytes(size)} already downloaded.");
            return new Unfinished(path, Ranges.Invert(missing, size), false, false, length, info.LastWriteTimeUtc);
        }

        // No progress file: a browser (or another program) downloaded the beginning of the file in one piece.
        // Not so if parts of it were never written - then it's not known which parts are downloaded.
        long keep = Math.Max(0, length - TailRedo);
        using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            if (Native.HasHoles(handle, keep))
            {
                Skip(path, $"{name} has parts that were never written and no progress file (.mirrordl or .aria2), so it's not known which parts of it are downloaded.",
                     "Put its progress file next to it, or move it away to download from scratch.");
                return null;
            }
        Write($"Found {name}: it holds the first {Format.Bytes(length)} of {Format.Bytes(size)}.");
        return new Unfinished(path, keep > 0 ? [(0, keep)] : [], true, PartFinder.IsFirefoxPart(path), length, info.LastWriteTimeUtc);
    }

    /// <summary>
    /// Leaves an unfinished file out of the download. If it was given by the user or has the download's own name,
    /// the download stops when no other file can be used: the user meant to continue it, or a new download would
    /// go where it is. Otherwise it is only noted.
    /// </summary>
    void Skip(string path, string why, string advice = "")
    {
        if (_job.PartFiles.Any(p => Path.GetFullPath(p).Equals(path, StringComparison.OrdinalIgnoreCase)) || PartFinder.HasName(path, _names))
            _refusal ??= $"{why} {advice}".TrimEnd();
        _skipped.Add(why);
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
    /// Compares a few pieces of every link, and of every unfinished file, so that only links with the same file
    /// are used and a different file is never continued. Returns the unfinished files that hold this file, with
    /// what they really hold: as found, or - for a Firefox file whose end a crash left empty - its good beginning.
    /// </summary>
    async Task<List<Unfinished>> CompareContentAsync(HttpClient http, List<Mirror> mirrors, List<Unfinished> files,
        long size, CancellationToken ct)
    {
        int length = (int)Math.Min(Mirror.SampleLength, size);
        var offsets = SampleOffsets(size, length, files);
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
        _pieces = (source.Samples!, offsets, length);

        var usable = new List<Unfinished>();
        foreach (var file in files)
        {
            Unfinished? result;
            try
            {
                result = file.Bytes == 0 ? file : await CheckFileAsync(http, source, file, offsets, length, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Skip(file.FilePath, $"{file.Name} can't be read ({e.Message.TrimEnd('.')}).");
                continue;
            }
            if (result != null) usable.Add(result);
        }
        return usable;
    }

    /// <summary>
    /// Compares the pieces of an unfinished file that lie in what it holds with the links. Returns the file with what
    /// it really holds - as found, or for a Firefox file whose end a crash left empty, its good beginning - or null
    /// when it is another file.
    /// </summary>
    async Task<Unfinished?> CheckFileAsync(HttpClient http, Mirror source, Unfinished file, List<long> offsets, int length, CancellationToken ct)
    {
        var reference = source.Samples!;
        using var part = File.OpenHandle(file.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[length];
        int compared = 0;
        long lastGood = -1;
        for (int i = 0; i < offsets.Count; i++)
        {
            long offset = offsets[i];
            if (!file.Have.Any(r => r.Start <= offset && offset + length <= r.End)) continue;
            ReadAt(part, buffer, offset);
            if (SHA256.HashData(buffer).AsSpan().SequenceEqual(reference[i]))
            {
                compared++;
                lastGood = offset;
                continue;
            }
            long differs = offset;
            // Firefox writes from the start to the end; after a crash its file can end in bytes that never reached
            // the disk and read as zeros. Then the part before them is good, and only the rest is needed again.
            if (file.Firefox && file.Have is [(0, var kept)])
            {
                try
                {
                    long good = await GoodPrefixAsync(http, source, part, lastGood, offset, length, ct);
                    Write($"The last {Format.Bytes(kept - good)} of {file.Name} never reached the disk " +
                          "(Firefox stopped before it could write them), so they aren't used.", LogKind.Warn);
                    return file with { Have = good > 0 ? [(0, good)] : [], Matched = good > 0 };
                }
                catch (DifferentFile e)
                {
                    differs = e.At;
                }
            }
            string where = differs == 0 ? "from its first byte" : $"at {Format.Bytes(differs)}";
            Skip(file.FilePath, $"{file.Name} is not the same file as the links (it differs {where}).",
                 "It is probably another file or version - move or delete it to download from scratch.");
            return null;
        }
        if (compared == 0)
        {
            // Only bits smaller than a piece: too little to tell that it is this file, and too little to matter.
            Write($"{file.Name} holds too little to compare; it is downloaded again.", LogKind.Dim);
            return file with { Have = [] };
        }
        Write($"{file.Name} matches the links ({Format.Plural(compared, "piece")} compared).", LogKind.Ok);
        return file with { Matched = true };
    }

    /// <summary>
    /// Where the good beginning of a Firefox file ends. <paramref name="bad"/> is a piece that differs from the
    /// links, <paramref name="good"/> one before it that matches (-1 if none). Each piece looked at is compared
    /// byte by byte: it must match, or match up to a point and be zeros from there to its end - the bytes that
    /// never reached the disk. Anything else means another file: <see cref="DifferentFile"/>. Between the two, a
    /// binary search: about 20 pieces of 64 KB even for 100 GB.
    /// </summary>
    static async Task<long> GoodPrefixAsync(HttpClient http, Mirror source, SafeFileHandle part, long good,
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
            throw new DifferentFile(offset + same);
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

    /// <summary>
    /// Where to compare: start, a third, two thirds, end, and inside what each unfinished file holds - for a file
    /// without a progress file, all over what it holds, since nothing else tells that it is in one piece.
    /// </summary>
    static List<long> SampleOffsets(long size, int length, List<Unfinished> files)
    {
        var offsets = new SortedSet<long>();
        void Add(long offset) => offsets.Add(Math.Clamp(offset, 0, size - length));
        Add(0);
        Add(size / 3);
        Add(size / 3 * 2);
        Add(size - length);
        foreach (var file in files)
            foreach (var (start, end) in file.Have.Where(r => r.End - r.Start >= length).OrderByDescending(r => r.End - r.Start).Take(2))
            {
                int points = file.OnePiece ? 8 : 1;
                for (int i = 1; i <= points; i++) Add(start + (end - start - length) * i / (points + 1));
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
        string? damage = null; // what the finished file doesn't match
        if (DiffersFromLinks(target.PartPath) is long differs and >= 0)
        {
            ok = false;
            damage = "the links";
            Write($"The finished file does NOT match the links at {Format.Bytes(differs)}!", LogKind.Error);
        }
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
                    damage ??= "the checksum";
                    Write($"{algorithm} does NOT match!\n  expected {expected}\n  got      {actual}", LogKind.Error);
                }
            }
        }
        File.Delete(target.StatePath);
        File.Delete(target.Aria2Path);

        // Before the file gets its name, so that a leftover can't take the name first.
        if (ok) CleanUp();
        else if (_spare.Any(f => File.Exists(f.FilePath)))
            Write($"The other unfinished files of this download are kept, since the finished file doesn't match {damage}.", LogKind.Warn);

        string path = target.FinalPath;
        if (!string.Equals(Path.GetFullPath(target.PartPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(target.FinalDir);
            path = FreePath(path);
            File.Move(target.PartPath, path);
        }
        SavedPath = path;
        if (ok) Write($"Saved: {path}", LogKind.Ok);
        else Write($"Saved, but it doesn't match {damage}, so the file is probably damaged: {path}", LogKind.Warn);
        if (_job.Checksums.Count == 0)
            Write("Tip: paste the checksum from the download page together with the links, and the whole file gets checked.", LogKind.Dim);
        return ok;
    }

    /// <summary>
    /// Compares the finished file with the links at the pieces compared before the download. Their fingerprints
    /// are known already, so this reads only those pieces of the file. Where it differs, or -1 (also when nothing
    /// was compared before).
    /// </summary>
    long DiffersFromLinks(string path)
    {
        if (_pieces is not { } pieces) return -1;
        var (reference, offsets, length) = pieces;
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[length];
        for (int i = 0; i < offsets.Count; i++)
        {
            ReadAt(file, buffer, offsets[i]);
            if (!SHA256.HashData(buffer).AsSpan().SequenceEqual(reference[i])) return offsets[i];
        }
        return -1;
    }

    /// <summary>
    /// Removes the other unfinished files of this download, with their progress files and Firefox's empty
    /// placeholders - to the Recycle Bin where the drive has one. Only called once the finished file is complete
    /// and checked; each of these files was found to be this download, and one that changed since or that
    /// another program has open is kept.
    /// </summary>
    void CleanUp()
    {
        var removed = new List<string>();
        foreach (var other in _spare)
        {
            var info = new FileInfo(other.FilePath);
            if (!info.Exists) continue;
            string? keep = !other.Matched ? "too little of it could be compared to be sure it is this download"
                : info.Length != other.Length || info.LastWriteTimeUtc != other.Written ? "it changed since it was checked"
                : InUse(other.FilePath) ? "another program has it open"
                : null;
            if (keep != null)
            {
                Write($"Kept {other.Name}: {keep}.", LogKind.Warn);
                continue;
            }
            string dir = Path.GetDirectoryName(other.FilePath)!;
            var placeholders = PartFinder.FinalNames(other.Name, copies: false).Select(n => Path.Combine(dir, n))
                .Where(p => !p.Equals(other.FilePath, StringComparison.OrdinalIgnoreCase) && new FileInfo(p) is { Exists: true, Length: 0 });
            // The file itself first: if it can't go, its progress file stays with it - without one, nothing would
            // tell anymore which parts of it are downloaded.
            if (!Native.Recycle(other.FilePath))
            {
                Write($"Could not remove {other.Name}; it stays where it is, with its progress file.", LogKind.Warn);
                continue;
            }
            removed.Add(other.Name);
            foreach (string file in placeholders.Prepend(other.FilePath + ".aria2").Prepend(other.FilePath + ".mirrordl"))
            {
                if (!File.Exists(file)) continue;
                if (Native.Recycle(file)) removed.Add(Path.GetFileName(file));
                else Write($"Could not remove {Path.GetFileName(file)}; it stays where it is.", LogKind.Warn);
            }
        }
        if (removed.Count > 0)
            Write($"Removed the other unfinished files of this download, which aren't needed anymore (to the Recycle Bin, " +
                  $"where the drive has one): {string.Join(", ", removed)}", LogKind.Info);
    }

    static bool InUse(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
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
