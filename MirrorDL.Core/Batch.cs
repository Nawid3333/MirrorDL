// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Collections.Concurrent;

namespace MirrorDL;

/// <summary>Where a file of a <see cref="Batch"/> is.</summary>
public enum FileState { Waiting, Running, Done, Checked, Mismatch, Paused, Failed, Removed }

/// <summary>One file of a <see cref="Batch"/>: its links, its own <see cref="MirrorDL.Job"/>, and how its download went.</summary>
public sealed class BatchFile
{
    readonly int[] _numbers;
    List<Mirror> _links;
    bool _linksUsed;
    volatile FileState _state;
    volatile Session? _session;

    internal BatchFile(int number, string name, long? size, Job job, List<Mirror> links)
    {
        Number = number;
        Name = name;
        Size = size;
        Job = job;
        _links = links;
        _numbers = [.. links.Select(m => m.Index)];
    }

    BatchFile(SavedFile saved)
    {
        Number = saved.Number;
        Name = saved.Name;
        Size = saved.Size;
        Job = saved.Job.ToJob();
        _numbers = [.. saved.Links];
        _links = NewLinks();
        _linksUsed = true;
        // A download that was going when MirrorDL closed is paused: its progress file says how far it got.
        _state = saved.State is FileState.Running ? FileState.Paused : saved.State;
        SavedPath = saved.SavedPath;
        Message = saved.Message;
        ChecksumMatched = saved.ChecksumMatched;
        CheckedOnly = saved.State == FileState.Checked || saved.CheckedOnly;
        _pageOnly = saved.IsPage;
    }

    /// <summary>Its place in the batch, from 1.</summary>
    public int Number { get; }
    public string Name { get; }
    public long? Size { get; }
    /// <summary>What it is downloaded with: its links, checksums, unfinished files and name.</summary>
    public Job Job { get; }
    /// <summary>Its links, numbered as they were pasted. New ones for each start, so that every start tries them all again.</summary>
    public IReadOnlyList<Mirror> Links => _links;
    /// <summary>Its download now, or the last one; null before the first.</summary>
    public Session? Session => _session;
    public FileState State { get => _state; internal set => _state = value; }
    /// <summary>Where the finished file is.</summary>
    public string? SavedPath { get; private set; }
    /// <summary>Why it failed, or what pausing means for it.</summary>
    public string? Message { get; private set; }
    /// <summary>The checksum it matched, if one was given and it did.</summary>
    public string? ChecksumMatched { get; private set; }
    /// <summary>It was there and complete already, so it was only checked.</summary>
    public bool CheckedOnly { get; private set; }
    public bool IsFinished => State is FileState.Done or FileState.Checked or FileState.Mismatch;
    /// <summary>
    /// Its links lead to web pages on which no link to a file could be found - one that needs a captcha or a click, say:
    /// nothing to download until the user opens a page in a browser and takes the link from there.
    /// </summary>
    public bool IsPage => _pageOnly || _links.Count > 0 && _links.All(m => m.IsPage);
    bool _pageOnly;

    internal List<Mirror> TakeLinks()
    {
        // The links inspected while sorting are used once; any later start inspects them anew.
        if (_linksUsed) _links = NewLinks();
        _linksUsed = true;
        return _links;
    }

    List<Mirror> NewLinks() => [.. _numbers.Zip(Job.Urls, (number, url) => new Mirror(number, url))];

    internal void Start(Session session)
    {
        _session = session;
        State = FileState.Running;
        Message = null;
    }

    internal void Fail(string message)
    {
        Message = message;
        State = FileState.Failed;
    }

    internal void End(int code, string? error = null)
    {
        var session = _session!;
        SavedPath = session.SavedPath;
        ChecksumMatched = session.ChecksumMatched;
        CheckedOnly = session.CheckedOnly;
        Message = code switch
        {
            0 or 2 => null,
            130 => session.Note,
            _ => error ?? session.ErrorMessage ?? "Failed.",
        };
        State = code switch
        {
            0 => session.CheckedOnly ? FileState.Checked : FileState.Done,
            2 => FileState.Mismatch,
            130 => FileState.Paused,
            _ => FileState.Failed,
        };
    }

    internal SavedFile ToSaved() =>
        new(Number, Name, Size, [.. _numbers], SavedJob.From(Job), State, SavedPath, Message, ChecksumMatched, CheckedOnly, IsPage);

    internal static BatchFile FromSaved(SavedFile saved) => new(saved);
}

/// <summary>
/// The links the user pasted at once, sorted into files: links to the same file - with the same name, or the same
/// size and content - are its mirrors and are used together; links to other files become files of their own, also
/// a link whose file can't be told (it doesn't work). Checksums and unfinished files go with the file they belong
/// to. The files are downloaded one after another, each with all of the internet line: that way the first ones are
/// done soonest, and the connections <see cref="Tuner"/> finds for one aren't taken by another.
/// </summary>
public sealed class Batch
{
    // Endings of web pages and scripts: "get.php" says nothing about the file behind it.
    static readonly HashSet<string> PageExtensions = new(
        [".php", ".asp", ".aspx", ".jsp", ".cgi", ".pl", ".py", ".do", ".action", ".htm", ".html"], StringComparer.OrdinalIgnoreCase);
    // The names of all files of batches that aren't finished, in their save folders, and whose they are: a file of
    // another batch with the same name gets a number instead of writing into the other's unfinished file.
    static readonly Dictionary<string, Batch> Claims = new(StringComparer.OrdinalIgnoreCase);

    readonly List<BatchFile> _files = [];
    readonly object _lock = new();
    List<Mirror> _mirrors;
    volatile BatchFile? _current;
    int _running;

    public Batch(Job job)
    {
        Job = job;
        _mirrors = NewMirrors();
    }

    /// <summary>All that was pasted.</summary>
    public Job Job { get; }
    /// <summary>All links, numbered from 1 as they were pasted.</summary>
    public IReadOnlyList<Mirror> Mirrors => _mirrors;
    /// <summary>The files, in the order of their first link; empty until the links are sorted.</summary>
    public IReadOnlyList<BatchFile> Files { get { lock (_lock) return [.. _files]; } }
    public bool IsSorted { get; private set; }
    public bool IsRunning => Volatile.Read(ref _running) == 1;
    /// <summary>The file being downloaded.</summary>
    public BatchFile? Current => _current;

    /// <summary>A message, of the batch (file null) or of one of its files. Raised on a background thread.</summary>
    public event Action<BatchFile?, string, LogKind>? Log;
    /// <summary>The links are sorted into <see cref="Files"/>.</summary>
    public event Action? Sorted;
    public event Action<BatchFile>? FileStarted;
    public event Action<BatchFile, Phase>? PhaseChanged;
    /// <summary>A file's links were checked; <see cref="Mirror.Problem"/> tells which ones are used.</summary>
    public event Action<BatchFile>? LinksChecked;
    public event Action<BatchFile>? FileEnded;
    /// <summary>Asks the user a yes/no question about a file.</summary>
    public Func<BatchFile, string, Task<bool>> ConfirmAsync { get; set; } = (_, _) => Task.FromResult(false);
    /// <summary>For tests: answers the HTTP requests instead of the network.</summary>
    internal HttpMessageHandler? Handler { get; set; }
    /// <summary>For tests: what moving a file to the Recycle Bin does.</summary>
    internal Func<string, Native.Recycled> Recycle { get; set; } = Native.Recycle;

    /// <summary>
    /// Sorts the links (the first time) and downloads the files that aren't done, one after another. Pausing
    /// stops at the file being downloaded; the next run goes on there. A file that fails doesn't stop the others,
    /// and is tried again the next run. Returns 0 when all are done, 130 when paused, 1 when a file failed, 2 when
    /// one doesn't match its checksum or links.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) throw new InvalidOperationException("The batch runs already.");
        try
        {
            if (!IsSorted && !await SortCoreAsync(ct)) return 130;
            foreach (var file in Files)
            {
                if (ct.IsCancellationRequested) break;
                Session session;
                lock (_lock)
                {
                    if (file.State is not (FileState.Waiting or FileState.Paused or FileState.Failed)) continue; // done or removed
                    if (file.IsPage)
                    {
                        // Nothing to download there: the user opens the page in a browser.
                        file.Fail(PageMessage(file));
                        continue;
                    }
                    session = NewSession(file);
                    file.Start(session);
                }
                await RunFileAsync(file, session, ct);
                if (file.State == FileState.Paused) break;
            }
            var files = Files.Where(f => f.State != FileState.Removed).ToList();
            if (files.Count > 1) Write(null, Summary(files), files.All(f => f.State is FileState.Done or FileState.Checked) ? LogKind.Ok : LogKind.Warn);
            if (files.All(f => f.IsFinished))
            {
                Unmatched(files);
                Release();
            }
            return files.Any(f => f.State is FileState.Paused or FileState.Waiting) ? 130
                 : files.Any(f => f.State == FileState.Failed) ? 1
                 : files.Any(f => f.State == FileState.Mismatch) ? 2
                 : 0;
        }
        finally
        {
            _current = null;
            Volatile.Write(ref _running, 0);
        }
    }

    Session NewSession(BatchFile file)
    {
        var session = new Session(file.Job, file.TakeLinks())
        {
            PartOfBatch = true,
            Handler = Handler,
            Recycle = Recycle,
            ConfirmAsync = question => ConfirmAsync(file, question),
        };
        session.Log += (text, kind) => Log?.Invoke(file, text, kind);
        session.PhaseChanged += phase => PhaseChanged?.Invoke(file, phase);
        session.LinksChecked += () => LinksChecked?.Invoke(file);
        return session;
    }

    async Task RunFileAsync(BatchFile file, Session session, CancellationToken ct)
    {
        _current = file;
        FileStarted?.Invoke(file);
        try
        {
            file.End(await session.RunAsync(ct));
        }
        catch (Exception e)
        {
            // A bug: this file failed, but the others still get their turn.
            Write(file, "Unexpected error: " + e, LogKind.Error);
            file.End(-1, "Unexpected error: " + e.Message);
        }
        _current = null;
        FileEnded?.Invoke(file);
    }

    /// <summary>Takes a file out of the batch: it isn't downloaded. Not while it is being downloaded (false).</summary>
    public bool Remove(BatchFile file)
    {
        lock (_lock)
        {
            if (file.State == FileState.Running) return false;
            file.State = FileState.Removed;
        }
        if (Files.All(f => f.IsFinished || f.State == FileState.Removed)) Release();
        return true;
    }

    /// <summary>Gives up the names of its files: another batch may use them now. Once it is finished or taken off the list.</summary>
    public void Release()
    {
        lock (Claims)
            foreach (string key in Claims.Where(c => c.Value == this).Select(c => c.Key).ToList())
                Claims.Remove(key);
    }

    /// <summary>"3 files: 2 saved, 1 failed".</summary>
    public static string Summary(IReadOnlyCollection<BatchFile> files)
    {
        var parts = new List<string>();
        void Add(int n, string what) { if (n > 0) parts.Add($"{n} {what}"); }
        Add(files.Count(f => f.State == FileState.Done), "saved");
        Add(files.Count(f => f.State == FileState.Checked), "there already");
        Add(files.Count(f => f.State == FileState.Mismatch), "don't match");
        Add(files.Count(f => f.State == FileState.Failed), "failed");
        Add(files.Count(f => f.State == FileState.Paused), "paused");
        Add(files.Count(f => f.State is FileState.Waiting or FileState.Running), "waiting");
        return $"{Format.Plural(files.Count, "file")}: {string.Join(", ", parts)}";
    }

    /// <summary>Checksums pasted for several files that none of them matched: one of them is damaged, or it is another file's.</summary>
    void Unmatched(List<BatchFile> files)
    {
        var shared = files.Where(f => f.Job.ChecksumsOfSeveral).ToList();
        if (shared.Count == 0) return;
        var matched = files.Select(f => f.ChecksumMatched).OfType<string>().ToHashSet();
        foreach (string hex in shared[0].Job.Checksums.Where(h => !matched.Contains(h)))
            Write(null, $"No file matches the checksum {hex}: one of the files that matched none is damaged, or it is the checksum " +
                        "of another file.", LogKind.Warn);
    }

    /// <summary>
    /// Checks the links and sorts them into <see cref="Files"/>, without downloading: so that the user can see what
    /// the links lead to, and take out what isn't wanted (<see cref="Remove"/>), before <see cref="RunAsync"/>.
    /// False when cancelled; then the links are checked anew next time.
    /// </summary>
    public async Task<bool> SortAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) throw new InvalidOperationException("The batch runs already.");
        try
        {
            return IsSorted || await SortCoreAsync(ct);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    async Task<bool> SortCoreAsync(CancellationToken ct)
    {
        Write(null, $"MirrorDL {AppInfo.Version}", LogKind.Dim); // so a copied log says which version it came from
        if (Native.TestingNoRecycleBin)
            Write(null, "Test mode (MIRRORDL_TEST_NO_RECYCLE_BIN): no file is moved to the Recycle Bin.", LogKind.Warn);
        try
        {
            using var http = Session.CreateClient(Handler);
            await SortAsync(http, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _mirrors = NewMirrors(); // they are checked anew next time
            Write(null, "Cancelled.", LogKind.Warn);
            return false;
        }
    }

    async Task SortAsync(HttpClient http, CancellationToken ct)
    {
        Write(null, $"Checking {Format.Plural(_mirrors.Count, "link")}...");
        await Mirror.InspectAllAsync(http, _mirrors, ct);
        if (Job.LookIntoPages) await LookIntoPagesAsync(http, ct);
        // A page whose links were found isn't a file of its own; the links are.
        var links = _mirrors.Where(m => !(m.IsPage && _mirrors.Any(f => f.FoundOn == m.Url))).ToList();
        var groups = Job.OneFile ? [links] : await GroupAsync(http, links, ct);
        lock (_lock) _files.AddRange(MakeFiles(groups));
        IsSorted = true;
        Claim();
        if (_files.Count > 1)
            Write(null, $"The {Format.Plural(_mirrors.Count, "link")} lead to {_files.Count} files; they are downloaded one after another.");
        Sorted?.Invoke();
    }

    /// <summary>
    /// Links that lead to a web page - a file host's page with a download button, say: the links to files found on it
    /// join the links, as if pasted after them. A page whose link to the file only appears after a captcha or a click
    /// shows none; it stays, so that the user can open it in a browser.
    /// </summary>
    async Task LookIntoPagesAsync(HttpClient http, CancellationToken ct)
    {
        var pages = _mirrors.Where(m => m.Usable && m.IsPage).ToList();
        if (pages.Count == 0) return;
        var known = _mirrors.Select(m => m.Url).ToHashSet();
        var found = new List<Mirror>();
        foreach (var page in pages)
        {
            Write(null, $"Looking for links to files on {page.Url.Host}...");
            string? html = await ReadPageAsync(http, page.Url, ct);
            var candidates = (html == null ? [] : PageLinks.Find(html, page.FinalUrl ?? page.Url))
                .Where(known.Add).Select(url => new Mirror(0, url) { FoundOn = page.Url }).ToList();
            await Mirror.InspectAllAsync(http, candidates, ct);
            var files = candidates.Where(m => m.Usable && !m.IsPage).ToList();
            if (files.Count == 0)
            {
                page.Problem = "a web page with no link to a file on it that MirrorDL can see: open it in the browser";
                Write(null, $"{page.Url} is a web page, and no link to a file can be seen on it: one that only appears after a " +
                            "captcha or a click can't. Open the page in the browser and click its download button there.", LogKind.Warn);
                continue;
            }
            page.Problem = $"a web page: {Format.Plural(files.Count, "link")} to files found on it";
            Write(null, $"Found on {page.Url.Host}: " + string.Join(", ", files.Select(m => m.FileName)), LogKind.Ok);
            found.AddRange(files);
        }
        for (int i = 0; i < found.Count; i++) found[i].Index = _mirrors.Count + i + 1;
        _mirrors.AddRange(found);
    }

    static string PageMessage(BatchFile file) =>
        $"{file.Links[0].Url} is a web page; no link to a file can be seen on it. Open it in the browser and click its download button there.";

    /// <summary>A web page's text, at most 4 MB of it; null if it can't be had.</summary>
    static async Task<string?> ReadPageAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        const int Most = 4 << 20;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var text = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while (text.Length < Most && (read = await body.ReadAsync(buffer, timeout.Token)) > 0) text.Write(buffer, 0, read);
            var encoding = System.Text.Encoding.UTF8;
            try
            {
                if (response.Content.Headers.ContentType?.CharSet is { Length: > 0 } charset)
                    encoding = System.Text.Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException) { } // a name .NET doesn't know: UTF-8 is the best guess
            return encoding.GetString(text.GetBuffer(), 0, (int)text.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sorts links into files: links with the same name are mirrors of one file (one of them with another size or
    /// content serves an old or wrong file, and <see cref="Session"/> skips it); so are links with the same size and
    /// content under other names. A link that doesn't work goes with the file its address names, or is one of its own.
    /// The files are in the order of their first link.
    /// </summary>
    internal static async Task<List<List<Mirror>>> GroupAsync(HttpClient http, IReadOnlyList<Mirror> mirrors, CancellationToken ct)
    {
        var parent = mirrors.ToDictionary(m => m, m => m);
        Mirror Root(Mirror m)
        {
            while (parent[m] != m) m = parent[m] = parent[parent[m]];
            return m;
        }
        void Join(Mirror a, Mirror b)
        {
            (a, b) = (Root(a), Root(b));
            if (a == b) return;
            if (b.Index < a.Index) (a, b) = (b, a);
            parent[b] = a;
        }

        var named = new Dictionary<string, Mirror>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mirrors.Where(m => m.Usable))
            if (KnownName(m) is { } name && !named.TryAdd(name, m))
                Join(named[name], m);

        // Only links of the same size can have the same content; of each group of them, one is compared - one at a
        // time per server, which may allow only a few connections.
        var buckets = mirrors.Where(m => m.Usable && m.SupportsRanges && m.Size > 0).GroupBy(m => m.Size!.Value)
            .Where(bucket => bucket.Select(Root).Distinct().Count() > 1).ToList();
        var fingerprints = new List<(long Size, Mirror Group, string Fingerprint)>();
        var gates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        await Task.WhenAll(buckets.SelectMany(bucket => bucket.GroupBy(Root).Select(async group =>
        {
            long size = bucket.Key;
            int length = (int)Math.Min(Mirror.SampleLength, size);
            var offsets = Session.SampleOffsets(size, length);
            foreach (var m in group.OrderBy(m => m.Index))
            {
                var gate = gates.GetOrAdd(m.Url.Authority, _ => new SemaphoreSlim(1));
                await gate.WaitAsync(ct);
                try { await m.SampleAsync(http, offsets, length, ct); }
                finally { gate.Release(); }
                if (m.Samples == null) continue; // it failed: the next one of the group
                lock (fingerprints) fingerprints.Add((size, group.Key, m.Fingerprint));
                break;
            }
        })));
        foreach (var same in fingerprints.GroupBy(f => (f.Size, f.Fingerprint)))
            foreach (var other in same.Skip(1))
                Join(same.First().Group, other.Group);

        foreach (var m in mirrors.Where(m => !m.Usable))
        {
            string name = UrlName(m.Url);
            if (!IsFileName(name)) continue;
            if (named.TryGetValue(name, out var file)) Join(file, m);
            else named[name] = m;
        }
        return [.. mirrors.GroupBy(Root).Select(g => g.OrderBy(m => m.Index).ToList()).OrderBy(g => g[0].Index)];
    }

    /// <summary>The name a link gives its file if it tells which file it is: sent by the server, or a file name in the address.</summary>
    internal static string? KnownName(Mirror m) => m.FileName is { } name && (m.NameFromHeader || IsFileName(name)) ? name : null;

    /// <summary>
    /// Whether a name from an address tells which file it is: it has an ending, and not a web page's or script's.
    /// "download" or "get.php" can stand for any file.
    /// </summary>
    internal static bool IsFileName(string name) => Path.GetExtension(name) is { Length: > 1 } ext && !PageExtensions.Contains(ext);

    static string UrlName(Uri url) => Mirror.CleanFileName(Uri.UnescapeDataString(url.Segments[^1]));

    /// <summary>The files of the groups, each with its name, its checksums and its unfinished files.</summary>
    List<BatchFile> MakeFiles(List<List<Mirror>> groups)
    {
        // The size and name each file gets, as Session decides them: the size most links have, the name the server sends.
        var chosen = groups.Select(links => links.Where(m => m.Usable).GroupBy(m => m.Size)
            .OrderByDescending(g => g.Key != null).ThenByDescending(g => g.Count()).ThenBy(g => g.Min(m => m.Index))
            .FirstOrDefault()?.ToList() ?? []).ToList();
        var sizes = chosen.Select(c => c.FirstOrDefault()?.Size).ToList();
        var naturals = chosen.Select((c, i) => c.FirstOrDefault(m => m.NameFromHeader)?.FileName
                                              ?? c.Select(m => m.FileName).OfType<string>().FirstOrDefault()
                                              ?? UrlName(groups[i][0].Url)).ToList();
        bool Works(int i) => groups[i].Any(m => m.Usable);

        // Unfinished files: with the file of their name; with the only file; or with the file of their size.
        var parts = groups.Select(_ => new List<string>()).ToList();
        foreach (string given in Job.PartFiles)
        {
            string path = Path.GetFullPath(given);
            var names = PartFinder.FinalNames(Path.GetFileName(path)).ToList();
            var candidates = Enumerable.Range(0, groups.Count).Where(Works).ToList();
            var byName = candidates.Where(i => groups[i].Any(m => m.FileName is { } n && names.Contains(n, StringComparer.OrdinalIgnoreCase))
                                               || names.Contains(naturals[i], StringComparer.OrdinalIgnoreCase)).ToList();
            if (byName.Count > 0) candidates = byName;
            if (candidates.Count > 1 && SizeOf(path) is long size) candidates = candidates.Where(i => sizes[i] == size).ToList();
            if (candidates.Count == 1)
                parts[candidates[0]].Add(path);
            else
                Write(null, $"{Path.GetFileName(path)}: it can't be told which of the files it is an unfinished download of, so it " +
                            "isn't used. Start it with only the links of its file.", LogKind.Warn);
        }

        // Names: a file of this or another unfinished batch in the same folder keeps its name; this one gets a number.
        var dirs = parts.Select(p => SaveDir(Job, p)).ToList();
        var names2 = new List<string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (Claims)
            for (int i = 0; i < groups.Count; i++)
            {
                string name = naturals[i];
                for (int n = 1; taken.Contains(Path.Combine(dirs[i], name)) || Claims.ContainsKey(Path.Combine(dirs[i], name)); n++)
                    name = Numbered(naturals[i], n);
                taken.Add(Path.Combine(dirs[i], name));
                names2.Add(name);
            }

        // Checksums: on the line of a link, that link's file's; with a name, that file's; the rest, of the files without one.
        var own = groups.Select(_ => new List<string>()).ToList();
        var loose = new List<string>(Job.Checksums);
        foreach (var c in Job.FileChecksums)
        {
            int at = c.Link != null
                ? groups.FindIndex(g => g.Any(m => m.Url == c.Link))
                : Enumerable.Range(0, groups.Count).FirstOrDefault(i => string.Equals(names2[i], c.Name, StringComparison.OrdinalIgnoreCase)
                      || string.Equals(naturals[i], c.Name, StringComparison.OrdinalIgnoreCase)
                      || groups[i].Any(m => string.Equals(m.FileName, c.Name, StringComparison.OrdinalIgnoreCase)), -1);
            var into = at >= 0 ? own[at] : loose;
            if (!into.Contains(c.Hex)) into.Add(c.Hex);
        }
        int open = Enumerable.Range(0, groups.Count).Count(i => own[i].Count == 0 && Works(i));

        var files = new List<BatchFile>();
        for (int i = 0; i < groups.Count; i++)
        {
            var job = new Job
            {
                OutDir = Job.OutDir,
                Connections = Job.Connections,
                PerServer = Job.PerServer,
                DeleteLeftovers = Job.DeleteLeftovers,
                Name = names2[i],
                ChecksumsOfSeveral = own[i].Count == 0 && open > 1,
            };
            job.Urls.AddRange(groups[i].Select(m => m.Url));
            job.PartFiles.AddRange(parts[i]);
            job.Checksums.AddRange(own[i].Count > 0 ? own[i] : loose);
            job.OtherNames.AddRange(Enumerable.Range(0, groups.Count).Where(j => j != i && dirs[j].Equals(dirs[i], StringComparison.OrdinalIgnoreCase))
                .Select(j => names2[j]));
            lock (Claims)
                job.OtherNames.AddRange(Claims.Keys.Where(k => Path.GetDirectoryName(k)!.Equals(dirs[i], StringComparison.OrdinalIgnoreCase))
                    .Select(k => Path.GetFileName(k)));
            files.Add(new BatchFile(i + 1, names2[i], sizes[i], job, groups[i]));
        }
        return files;
    }

    /// <summary>The size of the file an unfinished file is for, as its progress file says; a finished file's own; null if unknown.</summary>
    static long? SizeOf(string path)
    {
        try
        {
            return PartState.Load(path + ".mirrordl")?.Size ?? Aria2Control.Load(path + ".aria2")?.Size
                ?? (!PartFinder.IsUnfinished(path) && File.Exists(path) ? new FileInfo(path).Length : null);
        }
        catch (Exception e) when (e is UserError or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Where a file goes, as <see cref="Session"/> decides it: the save folder, or next to its unfinished file.</summary>
    static string SaveDir(Job job, IReadOnlyList<string> parts) =>
        Path.GetFullPath(job.OutDir ?? (parts.Count > 0 ? Path.GetDirectoryName(parts[0])! : Native.DownloadsFolder()));

    /// <summary>"name (n).ext", as a browser numbers a second download of the same name.</summary>
    static string Numbered(string name, int n) => $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}";

    /// <summary>Claims the names of its unfinished files, until <see cref="Release"/>.</summary>
    void Claim()
    {
        lock (Claims)
            foreach (var file in Files.Where(f => !f.IsFinished && f.State != FileState.Removed))
                Claims.TryAdd(Path.Combine(SaveDir(file.Job, file.Job.PartFiles), file.Name), this);
    }

    List<Mirror> NewMirrors() => [.. Job.Urls.Select((url, i) => new Mirror(i + 1, url))];

    void Write(BatchFile? file, string text, LogKind kind = LogKind.Info) => Log?.Invoke(file, text, kind);

    /// <summary>The batch as it is now, to be continued after MirrorDL was closed (see <see cref="FromSaved"/>).</summary>
    public SavedBatch ToSaved() => new(SavedJob.From(Job), IsSorted ? [.. Files.Select(f => f.ToSaved())] : null);

    /// <summary>A batch saved before: its files as they were; the links are checked again when it runs.</summary>
    public static Batch FromSaved(SavedBatch saved)
    {
        var batch = new Batch(saved.Job.ToJob());
        if (saved.Files is { } files)
        {
            batch._files.AddRange(files.Select(BatchFile.FromSaved));
            batch.IsSorted = true;
            batch.Claim();
        }
        return batch;
    }
}

/// <summary>A <see cref="Batch"/> as it is kept between runs of MirrorDL.</summary>
public sealed record SavedBatch(SavedJob Job, List<SavedFile>? Files);

public sealed record SavedFile(int Number, string Name, long? Size, List<int> Links, SavedJob Job, FileState State,
    string? SavedPath, string? Message, string? ChecksumMatched, bool CheckedOnly = false, bool IsPage = false);

public sealed record SavedChecksum(string Hex, string? Name, string? Link);

public sealed record SavedJob(List<string> Urls, List<string> Checksums, List<SavedChecksum>? FileChecksums, List<string> PartFiles,
    string? OutDir, int? Connections, int? PerServer, bool DeleteLeftovers, bool ChecksumsOfSeveral, string? Name, List<string>? OtherNames,
    bool OneFile = false, bool LookIntoPages = true)
{
    public static SavedJob From(Job job) => new(
        [.. job.Urls.Select(u => u.AbsoluteUri)], [.. job.Checksums],
        [.. job.FileChecksums.Select(c => new SavedChecksum(c.Hex, c.Name, c.Link?.AbsoluteUri))], [.. job.PartFiles],
        job.OutDir, job.Connections, job.PerServer, job.DeleteLeftovers, job.ChecksumsOfSeveral, job.Name, [.. job.OtherNames], job.OneFile, job.LookIntoPages);

    public Job ToJob()
    {
        var job = new Job
        {
            OutDir = OutDir,
            Connections = Connections,
            PerServer = PerServer,
            DeleteLeftovers = DeleteLeftovers,
            ChecksumsOfSeveral = ChecksumsOfSeveral,
            Name = Name,
            OneFile = OneFile,
            LookIntoPages = LookIntoPages,
        };
        job.Urls.AddRange(Urls.Select(u => new Uri(u)));
        job.Checksums.AddRange(Checksums);
        job.FileChecksums.AddRange((FileChecksums ?? []).Select(c => new FileChecksum(c.Hex, c.Name, c.Link is { } l ? new Uri(l) : null)));
        job.PartFiles.AddRange(PartFiles);
        job.OtherNames.AddRange(OtherNames ?? []);
        return job;
    }
}
