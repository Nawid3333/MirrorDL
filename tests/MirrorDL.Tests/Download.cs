// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Security.Cryptography;

namespace MirrorDL.Tests;

/// <summary>
/// A whole download against a <see cref="FakeServer"/>, saved in a folder of its own. Files "moved to the Recycle
/// Bin" go to a folder of the test instead of the real one, and questions get the answer the test sets.
/// </summary>
sealed class Download : IDisposable
{
    readonly TempFolder _bin = new();
    int _links;

    public TempFolder Folder { get; } = new();
    public FakeServer Server { get; } = new();
    public Job Job { get; } = new();
    public Session Session { get; private set; } = null!;
    public List<string> Log { get; } = [];
    public List<string> Recycled { get; } = [];
    public List<string> Questions { get; } = [];
    /// <summary>What moving a file to the Recycle Bin gives; anything but Yes leaves the file where it is.</summary>
    public Native.Recycled RecycleResult { get; set; } = Native.Recycled.Yes;
    /// <summary>The answer to every question.</summary>
    public bool Answer { get; set; }
    /// <summary>Called when the download goes into a new phase, on its thread.</summary>
    public Action<Phase>? OnPhase { get; set; }

    public Download() => Job.OutDir = Folder.Path;

    public string Output { get { lock (Log) return string.Join("\n", Log); } }

    /// <summary>A link serving <paramref name="content"/> at an address ending in <paramref name="name"/>.</summary>
    public FakeLink Link(byte[] content, string name = "data.bin")
    {
        string url = $"http://mirror{++_links}.test/{name}";
        Job.Urls.Add(new Uri(url));
        return Server.Add(url, content);
    }

    public string PathOf(string name) => Path.Combine(Folder.Path, name);

    /// <summary>The names of the files in the folder, sorted.</summary>
    public string[] Files => [.. Directory.GetFiles(Folder.Path).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    /// <summary>Runs the download to its end; cancelled only with the test.</summary>
    public Task<int> RunAsync() => RunCoreAsync(TestContext.Current.CancellationToken);

    /// <summary>Runs the download; cancelling <paramref name="pause"/> pauses it, as the Pause button does.</summary>
    public async Task<int> RunAsync(CancellationTokenSource pause)
    {
        using var both = CancellationTokenSource.CreateLinkedTokenSource(pause.Token, TestContext.Current.CancellationToken);
        return await RunCoreAsync(both.Token);
    }

    async Task<int> RunCoreAsync(CancellationToken ct)
    {
        Session = new Session(Job)
        {
            Handler = Server,
            Recycle = MoveToBin,
            ConfirmAsync = question =>
            {
                lock (Questions) Questions.Add(question);
                return Task.FromResult(Answer);
            },
        };
        Session.Log += (text, _) => { lock (Log) Log.Add(text); };
        Session.PhaseChanged += phase => OnPhase?.Invoke(phase);
        return await Session.RunAsync(ct);
    }

    Native.Recycled MoveToBin(string path)
    {
        if (RecycleResult != Native.Recycled.Yes) return RecycleResult;
        File.Move(path, Path.Combine(_bin.Path, Guid.NewGuid().ToString("N")));
        lock (Recycled) Recycled.Add(Path.GetFileName(path));
        return Native.Recycled.Yes;
    }

    public void Dispose()
    {
        Folder.Dispose();
        _bin.Dispose();
        Server.Dispose();
    }

    /// <summary>Random bytes, the same for the same seed.</summary>
    public static byte[] Bytes(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
