// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Net;

namespace MirrorDL.Tests;

/// <summary>Links that fail: before the download, and in the middle of it. The good links always finish the file.</summary>
public class EngineFaultTests
{
    const int MB = 1 << 20;
    const int Size = 3 * MB + 12_345;
    static readonly byte[] File1 = Download.Bytes(Size, 1);

    [Fact]
    public async Task A_dead_link_is_skipped()
    {
        using var d = new Download();
        d.Link(File1);
        d.Job.Urls.Add(new Uri("http://nowhere.test/data.bin")); // the fake server answers 404

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Equal("HTTP 404 Not Found", d.Session.Mirrors[1].Problem);
    }

    [Fact]
    public async Task No_working_link_is_an_error()
    {
        using var d = new Download();
        d.Job.Urls.Add(new Uri("http://nowhere.test/data.bin"));

        Assert.Equal(1, await d.RunAsync());
        Assert.Equal("None of the links work.", d.Session.ErrorMessage);
        Assert.Equal(Phase.Failed, d.Session.Phase);
        Assert.Empty(d.Files);
    }

    [Fact]
    public async Task A_link_that_sends_the_wrong_part_while_comparing_is_dropped()
    {
        using var d = new Download();
        d.Link(File1);
        d.Link(File1).Shift = 1;

        Assert.Equal(0, await d.RunAsync());
        Assert.Contains("wrong part", d.Session.Mirrors[1].Problem);
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
    }

    [Fact]
    public async Task A_connection_that_breaks_off_is_tried_again()
    {
        using var d = new Download();
        var link = d.Link(File1);
        d.OnPhase = phase => { if (phase == Phase.Downloading) link.DropAfter = 300_000; };

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Null(d.Session.Mirrors[0].Problem);
        Assert.Contains("connection reset", d.Session.Mirrors[0].LastError);
    }

    [Theory]
    [InlineData("shift", "wrong part")]          // Content-Range starts elsewhere than asked
    [InlineData("grow", "file on the server changed")]
    [InlineData("whole", "ignored the range request")]
    public async Task A_link_that_goes_wrong_during_the_download_is_dropped_and_the_others_finish(string fault, string problem)
    {
        using var d = new Download();
        d.Link(File1);
        var bad = d.Link(File1);
        d.OnPhase = phase =>
        {
            if (phase != Phase.Downloading) return;
            if (fault == "shift") bad.Shift = 1;
            else if (fault == "grow") bad.Grow = 1;
            else bad.Ranges = false;
        };

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Contains(problem, d.Session.Mirrors[1].Problem);
        Assert.Equal(0, d.Server.Open); // also the answers it gave up on are closed
    }

    [Fact]
    public async Task A_link_that_refuses_extra_connections_goes_on_with_the_ones_it_has()
    {
        // A slow link whose host allows one connection at once: it answers 503 to the extra connections MirrorDL
        // opens once data flows; the connection that works goes on, and later ones take its place.
        using var d = new Download();
        byte[] big = Download.Bytes(6 * MB, 4);
        var link = d.Link(big);
        // About 2 MB/s: when the next connections come, enough is left to split (2 MB at least), so they ask.
        link.ChunkDelay = TimeSpan.FromMilliseconds(30);
        d.OnPhase = phase => { if (phase == Phase.Downloading) link.MaxConnections = 1; };

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(big, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Null(d.Session.Mirrors[0].Problem);
        Assert.True(link.Refused >= 1, "no extra connection was tried"); // tried, refused - and the link went on
    }

    [Fact]
    public async Task A_link_that_is_busy_for_a_moment_keeps_its_connections()
    {
        // The first two extra connections are answered "busy" (503), then the server has room again.
        using var d = new Download();
        int requests = 0;
        var link = d.Link(Download.Bytes(6 * MB, 6));
        link.ChunkDelay = TimeSpan.FromMilliseconds(30); // about 2 MB/s: still running when more connections come
        d.OnPhase = phase =>
        {
            if (phase != Phase.Downloading) return;
            link.Answer = (_, _) => Interlocked.Increment(ref requests) is 2 or 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : null;
        };

        Assert.Equal(0, await d.RunAsync());
        Assert.True(requests > 3, "no extra connection was tried");
        // They waited the moment out instead of leaving: the link may still have as many as it started with.
        Assert.True(d.Session.Mirrors[0].Cap >= Tuner.StartPerLink, $"the link was cut to {d.Session.Mirrors[0].Cap} connections");
    }

    [Theory]
    [InlineData(1)] // the very first question, for the size
    [InlineData(2)] // a sample of the content
    public async Task A_link_that_is_busy_while_it_is_checked_is_asked_again_instead_of_left_out(int busy)
    {
        using var d = new Download();
        d.Link(File1);
        d.Link(File1).Answer = (_, n) => n == busy ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null;

        Assert.Equal(0, await d.RunAsync());
        Assert.Null(d.Session.Mirrors[1].Problem);
        Assert.True(d.Session.Mirrors[1].Bytes > 0, "the busy link was not used");
    }

    [Fact]
    public async Task A_server_that_refuses_samples_at_once_gets_them_one_at_a_time()
    {
        using var d = new Download();
        d.Link(File1);
        var strict = d.Link(File1);
        strict.ChunkDelay = TimeSpan.FromMilliseconds(50); // so that samples asked at once overlap
        strict.MaxConnections = 1;
        strict.LimitStatus = HttpStatusCode.Forbidden;     // "too many": not a busy moment that passes

        Assert.Equal(0, await d.RunAsync());
        Assert.True(strict.Refused >= 1, "the samples weren't asked for at once");
        Assert.Null(d.Session.Mirrors[1].Problem);
    }

    [Fact]
    public async Task A_link_that_slows_to_a_trickle_after_a_fast_start_has_its_rest_taken_over()
    {
        // Its average stays high for long after the fast start; its speed of the last seconds shows the trickle.
        using var d = new Download();
        byte[] file = Download.Bytes(12 * MB, 5);
        d.Link(file).ChunkDelay = TimeSpan.FromMilliseconds(10);  // about 6 MB/s
        var trickle = d.Link(file);
        trickle.SlowAfter = 2 * MB;                                 // 2 MB fast, then
        trickle.ChunkDelay = TimeSpan.FromMilliseconds(600);        // about 110 KB/s
        trickle.MaxConnections = 1;                                 // and one connection only, so that its rest
        trickle.LimitStatus = HttpStatusCode.Forbidden;             // stays with that one trickling connection

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(file, File.ReadAllBytes(d.PathOf("data.bin")));
        // Taken over once it has run 4 s, the download is done in about 5 s; judged by its average, the trickle kept
        // its last 2 MB and took about 20 s for them. 15 s leaves room for a slow, busy machine (a CI runner).
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"it took {watch.Elapsed.TotalSeconds:0.0} s: the trickling link's rest was not taken over");
    }

    [Fact]
    public async Task A_part_that_ends_early_without_an_error_is_asked_for_again()
    {
        using var d = new Download();
        var link = d.Link(File1);
        d.OnPhase = phase => { if (phase == Phase.Downloading) link.CutAfter = 300_000; };

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Contains("connection closed early", d.Session.Mirrors[0].LastError);
    }
}
