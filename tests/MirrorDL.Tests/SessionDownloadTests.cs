// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

namespace MirrorDL.Tests;

/// <summary>New downloads from start to end: links, names, checksums, servers without ranges, pausing.</summary>
public class SessionDownloadTests
{
    const int Size = 3 * (1 << 20) + 12_345; // a few segments, and not a round number
    static readonly byte[] File1 = Download.Bytes(Size, 1);

    [Fact]
    public async Task Downloads_from_several_links_into_a_file_under_the_links_name()
    {
        using var d = new Download();
        d.Link(File1);
        d.Link(File1);

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Equal(["data.bin"], d.Files); // no .part or progress file left
        Assert.Equal(d.PathOf("data.bin"), d.Session.SavedPath);
        Assert.Equal(Phase.Finished, d.Session.Phase);
        Assert.Equal(Size, d.Session.Mirrors.Sum(m => m.Bytes)); // every byte once
        var status = d.Session.GetStatus();
        Assert.Equal(100, status.Percent);
        Assert.Empty(status.Missing);
        Assert.Contains("Tip: paste the checksum", d.Output);
    }

    [Fact]
    public async Task A_name_the_server_sends_wins_over_the_end_of_the_address()
    {
        using var d = new Download();
        d.Link(File1, "get").Name = "real.iso";
        d.Link(File1, "get");

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(["real.iso"], d.Files);
    }

    [Fact]
    public async Task Links_with_other_content_or_another_size_are_skipped()
    {
        using var d = new Download();
        d.Link(File1);
        d.Link(File1);
        d.Link(Download.Bytes(Size, 2));     // same size, other content
        d.Link(Download.Bytes(Size + 1, 3)); // another size

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Contains("different content", d.Session.Mirrors[2].Problem);
        Assert.Contains("different size", d.Session.Mirrors[3].Problem);
        Assert.Equal(0, d.Session.Mirrors[2].Bytes + d.Session.Mirrors[3].Bytes);
    }

    [Fact]
    public async Task Links_that_dont_tell_the_size_never_outvote_one_that_does()
    {
        using var d = new Download();
        d.Link(File1);
        foreach (var link in new[] { d.Link(File1), d.Link(File1) })
            link.Answer = (_, _) =>
            {
                var content = new StreamContent(new MemoryStream(File1));
                content.Headers.ContentLength = null; // no Content-Length: the size is unknown
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content };
            };

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Null(d.Session.Mirrors[0].Problem);
        Assert.Contains("different size (unknown)", d.Session.Mirrors[1].Problem);
    }

    [Fact]
    public async Task A_matching_checksum_is_confirmed()
    {
        using var d = new Download();
        d.Link(File1);
        d.Job.Checksums.Add(Download.Sha256(File1));

        Assert.Equal(0, await d.RunAsync());
        Assert.Contains("SHA256 matches", d.Output);
        Assert.DoesNotContain("Tip:", d.Output);
    }

    [Fact]
    public async Task A_checksum_that_doesnt_match_is_said_and_the_file_still_saved()
    {
        using var d = new Download();
        d.Link(File1);
        d.Job.Checksums.Add(Download.Sha256(Download.Bytes(Size, 2)));

        Assert.Equal(2, await d.RunAsync());
        Assert.True(File.Exists(d.PathOf("data.bin")));
        Assert.Contains("SHA256 does NOT match", d.Output);
        Assert.Contains("doesn't match the checksum: either it is damaged, or the checksum is for another file", d.Output);
    }

    [Fact]
    public async Task A_server_without_range_requests_sends_the_whole_file_over_one_connection()
    {
        using var d = new Download();
        d.Link(File1).Ranges = false;

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Contains("can't send parts of the file", d.Output);
    }

    [Fact]
    public async Task An_empty_file_is_saved_too()
    {
        using var d = new Download();
        d.Link([]);
        d.Link([]);

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(0, new FileInfo(d.PathOf("data.bin")).Length);
        Assert.Equal(["data.bin"], d.Files);
    }

    [Fact]
    public async Task Pausing_keeps_the_progress_and_the_next_start_continues()
    {
        using var d = new Download();
        foreach (var link in new[] { d.Link(File1), d.Link(File1) })
            link.ChunkDelay = TimeSpan.FromMilliseconds(20); // slow enough to pause in the middle
        using var pause = new CancellationTokenSource();
        d.OnPhase = phase =>
        {
            if (phase != Phase.Downloading) return;
            var session = d.Session;
            _ = Task.Run(async () => // pause once a MB has arrived, as a user would
            {
                while (session.Mirrors.Sum(m => m.Bytes) < 1 << 20 && session.Phase == Phase.Downloading)
                    await Task.Delay(10, CancellationToken.None);
                pause.Cancel();
            }, CancellationToken.None);
        };

        Assert.Equal(130, await d.RunAsync(pause));
        Assert.Equal(Phase.Paused, d.Session.Phase);
        Assert.Equal(["data.bin.part", "data.bin.part.mirrordl"], d.Files);
        Assert.Contains("The progress is saved", d.Session.Note);
        long before = d.Session.Mirrors.Sum(m => m.Bytes);

        d.OnPhase = null;
        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Equal(["data.bin"], d.Files);
        Assert.Contains("Found data.bin.part (mirrordl progress)", d.Output);
        Assert.Equal(Size - before, d.Session.Mirrors.Sum(m => m.Bytes)); // only what wasn't there yet
    }

    [Fact]
    public async Task A_link_that_redirects_gives_the_name_of_where_it_ends_up()
    {
        using var d = new Download();
        d.Link(File1, "get").FinalUrl = new Uri("https://cdn.test/files/real.iso?token=abc");

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(["real.iso"], d.Files);
        Assert.Equal("mirror1.test → cdn.test", d.Session.Mirrors[0].Server);
    }

    [Fact]
    public async Task A_finished_name_that_is_taken_gets_a_number_and_the_file_there_stays()
    {
        using var d = new Download();
        File.WriteAllBytes(d.PathOf("data.bin"), [1, 2, 3]);
        d.Link(File1);

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal([1, 2, 3], File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data (1).bin")));
    }

    [Fact]
    public async Task An_empty_file_of_the_users_under_the_same_name_stays()
    {
        using var d = new Download();
        File.WriteAllBytes(d.PathOf("data.bin"), []); // no Firefox .part next to it: not Firefox's placeholder
        d.Link(File1);

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(0, new FileInfo(d.PathOf("data.bin")).Length);
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data (1).bin")));
    }
}
