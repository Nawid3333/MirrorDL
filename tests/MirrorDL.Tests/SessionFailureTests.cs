// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Net;

namespace MirrorDL.Tests;

/// <summary>
/// What goes wrong around a download - links that fail or answer oddly, files other programs hold, stopping at
/// any moment - and the download either ends in a clear message or still finishes.
/// </summary>
public class SessionFailureTests
{
    const int MB = 1 << 20;
    const int Size = 3 * MB + 12_345;
    static readonly byte[] File1 = Download.Bytes(Size, 1);

    static byte[] Holding(int length, params (int Start, int End)[] ranges)
    {
        var bytes = new byte[length];
        foreach (var (start, end) in ranges) Array.Copy(File1, start, bytes, start, end - start);
        return bytes;
    }

    /// <summary>MirrorDL's own file with the second MB, and a numbered Firefox copy with the first two.</summary>
    static string TwoUnfinished(Download d)
    {
        File.WriteAllBytes(d.PathOf("data.bin.part"), Holding(Size, (MB, 2 * MB)));
        new PartState(Size, [(0, MB), (2 * MB, Size)]).Save(d.PathOf("data.bin.part.mirrordl"));
        string spare = d.PathOf("data(1).Ab12Cd34.bin.part");
        File.WriteAllBytes(spare, File1[..(2 * MB)]);
        return spare;
    }

    [Fact]
    public async Task When_every_link_fails_during_the_download_the_progress_is_kept()
    {
        using var d = new Download();
        var link = d.Link(File1);
        d.OnPhase = phase => { if (phase == Phase.Downloading) link.Shift = 1; }; // from now on, wrong parts

        Assert.Equal(1, await d.RunAsync());
        Assert.Contains("All links failed. The progress is saved", d.Session.ErrorMessage);
        Assert.Equal(["data.bin.part", "data.bin.part.mirrordl"], d.Files);
        // Nothing of the wrong parts counts as downloaded.
        Assert.Equal([(0L, (long)Size)], PartState.Load(d.PathOf("data.bin.part.mirrordl"))!.Missing);
    }

    [Fact]
    public async Task Stopping_while_the_checksum_is_checked_says_that_the_download_itself_is_complete()
    {
        using var d = new Download();
        d.Link(File1);
        d.Job.Checksums.Add(Download.Sha256(File1));
        using var stop = new CancellationTokenSource();
        d.OnPhase = phase => { if (phase == Phase.Verifying) stop.Cancel(); };

        Assert.Equal(130, await d.RunAsync(stop));
        Assert.Equal(Phase.Cancelled, d.Session.Phase);
        Assert.Contains("The download itself is complete", d.Session.Note);
    }

    [Fact]
    public async Task Without_range_requests_a_server_error_ends_the_download_with_it()
    {
        using var d = new Download();
        var link = d.Link(File1);
        link.Ranges = false;
        d.OnPhase = phase =>
        {
            if (phase == Phase.Downloading)
                link.Answer = (_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        };

        Assert.Equal(1, await d.RunAsync());
        Assert.Contains("HTTP 500", d.Session.ErrorMessage);
    }

    [Fact]
    public async Task Without_range_requests_a_broken_connection_ends_the_download()
    {
        using var d = new Download();
        var link = d.Link(File1);
        link.Ranges = false;
        d.OnPhase = phase => { if (phase == Phase.Downloading) link.DropAfter = 500_000; };

        Assert.Equal(1, await d.RunAsync());
        Assert.Contains("Download failed", d.Session.ErrorMessage);
    }

    [Fact]
    public async Task Without_range_requests_a_body_that_ends_early_is_not_saved_as_complete()
    {
        using var d = new Download();
        var link = d.Link(File1);
        link.Ranges = false;
        d.OnPhase = phase => { if (phase == Phase.Downloading) link.CutAfter = 500_000; };

        Assert.Equal(1, await d.RunAsync());
        Assert.Contains("closed before the whole file arrived", d.Session.ErrorMessage);
        Assert.False(File.Exists(d.PathOf("data.bin")));
    }

    [Fact]
    public async Task Without_range_requests_stopping_keeps_the_file_and_says_it_starts_over_next_time()
    {
        using var d = new Download();
        d.Link(File1).Ranges = false;
        using var stop = new CancellationTokenSource();
        d.OnPhase = phase => { if (phase == Phase.Downloading) stop.Cancel(); };

        Assert.Equal(130, await d.RunAsync(stop));
        Assert.Equal(Phase.Cancelled, d.Session.Phase);
        Assert.Contains("this server can't continue a download", d.Session.Note);
    }

    [Fact]
    public async Task An_unfinished_file_another_program_writes_to_stops_the_download_with_a_hint()
    {
        using var d = new Download();
        File.WriteAllBytes(d.PathOf("data.bin.part"), File1[..(2 * MB)]);
        // Like Firefox still at it: others may read the file, but not write to it.
        using (new FileStream(d.PathOf("data.bin.part"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            d.Link(File1);
            Assert.Equal(1, await d.RunAsync());
        }
        Assert.Contains("is in use by another program", d.Session.ErrorMessage);
        Assert.Equal(File1[..(2 * MB)], File.ReadAllBytes(d.PathOf("data.bin.part")));
    }

    [Fact]
    public async Task A_spare_another_program_has_open_is_used_but_kept()
    {
        using var d = new Download();
        string spare = TwoUnfinished(d);
        d.Link(File1);
        using (new FileStream(spare, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Assert.Equal(0, await d.RunAsync());

        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Contains("Took 1.00 MB from data(1).Ab12Cd34.bin.part", d.Output);
        Assert.Empty(d.Recycled);
        Assert.Contains("Kept data(1).Ab12Cd34.bin.part: another program has it open", d.Output);
    }

    [Fact]
    public async Task A_spare_that_changed_after_it_was_compared_is_neither_copied_from_nor_removed()
    {
        using var d = new Download();
        string spare = TwoUnfinished(d);
        d.Link(File1);
        d.OnPhase = phase => { if (phase == Phase.Copying) File.SetLastWriteTimeUtc(spare, DateTime.UtcNow.AddHours(1)); };

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Contains("changed since it was compared; what it holds is downloaded instead", d.Output);
        Assert.Equal(Size - MB, d.Session.Mirrors.Sum(m => m.Bytes)); // only the own file's MB was not downloaded
        Assert.Contains("it changed since it was checked", d.Output);
        Assert.True(File.Exists(spare));
    }

    [Fact]
    public async Task A_leftover_that_cant_be_deleted_stays_and_says_so()
    {
        using var d = new Download { RecycleResult = Native.Recycled.NoRoom };
        string spare = TwoUnfinished(d);
        File.SetAttributes(spare, FileAttributes.ReadOnly);
        d.Link(File1);
        d.Job.DeleteLeftovers = true;

        Assert.Equal(0, await d.RunAsync());
        Assert.True(File.Exists(spare));
        Assert.Contains("Could not delete data(1).Ab12Cd34.bin.part", d.Output);
    }

    [Fact]
    public async Task A_progress_file_another_program_holds_at_the_end_doesnt_fail_the_download()
    {
        using var d = new Download();
        d.Link(File1);
        FileStream? scanner = null; // like a virus scanner: reads it, and doesn't let it be deleted meanwhile
        d.OnPhase = phase =>
        {
            if (phase == Phase.Finishing)
                scanner = new FileStream(d.PathOf("data.bin.part.mirrordl"), FileMode.Open, FileAccess.Read, FileShare.Read);
        };
        try
        {
            Assert.Equal(0, await d.RunAsync());
        }
        finally
        {
            scanner?.Dispose();
        }
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Contains("Could not remove data.bin.part.mirrordl", d.Output);
    }

    [Fact]
    public async Task Odd_answers_to_the_first_question_leave_the_link_out_and_say_why()
    {
        using var d = new Download();
        d.Link(File1);
        d.Link(File1).Answer = (_, _) => new HttpResponseMessage(HttpStatusCode.NoContent);
        d.Link(File1).Answer = (_, _) => throw new HttpRequestException("No such host is known.");
        d.Link(File1).ContentType = "text/html";

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Equal("unexpected answer from the server", d.Session.Mirrors[1].Problem);
        Assert.Equal("No such host is known", d.Session.Mirrors[2].Problem);
        Assert.Equal("looks like a web page, not a file", d.Session.Mirrors[3].Note);
        Assert.Null(d.Session.Mirrors[3].Problem); // a note only: its content decides
    }

    [Fact]
    public async Task A_link_that_stops_sending_parts_while_comparing_is_left_out()
    {
        using var d = new Download();
        d.Link(File1);
        var odd = d.Link(File1);
        d.OnPhase = phase => { if (phase == Phase.Comparing) odd.Ranges = false; };

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal("content check failed: HTTP 200 OK", d.Session.Mirrors[1].Problem);
    }

    [Fact]
    public async Task A_fast_link_takes_over_what_a_slow_one_has_left_at_the_end()
    {
        using var d = new Download();
        var fast = d.Link(File1);
        var slow = d.Link(File1);
        fast.ChunkDelay = TimeSpan.FromMilliseconds(10);  // about 6 MB/s
        // About 110 KB/s: after the 4 s a part runs before it is judged, it would still need more than 5 s.
        slow.ChunkDelay = TimeSpan.FromMilliseconds(600);

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        var (f, s) = (d.Session.Mirrors[0].Bytes, d.Session.Mirrors[1].Bytes);
        Assert.Equal(Size, f + s);
        Assert.True(f > Size / 2 + MB / 2, $"fast {f}, slow {s}"); // it took over much of the slow link's half
    }
}
