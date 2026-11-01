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
