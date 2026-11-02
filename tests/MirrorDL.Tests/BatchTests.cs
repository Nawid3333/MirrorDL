// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Text.Json;

namespace MirrorDL.Tests;

/// <summary>Several files pasted at once: sorting the links into files, checksums and unfinished files, pausing, restarting.</summary>
public class BatchTests
{
    const int Size = 2 * (1 << 20) + 4_321;
    static readonly byte[] A = Download.Bytes(Size, 11), B = Download.Bytes(Size + 7, 12), C = Download.Bytes(Size + 13, 13);

    [Fact]
    public async Task Links_to_different_files_become_files_of_their_own_each_from_its_mirrors()
    {
        using var d = new Download();
        d.Link(A, "a.bin");
        d.Link(B, "b.bin");
        d.Link(A, "a.bin");
        d.Link(C, "c.bin");
        d.Link(B, "b.bin");

        Assert.Equal(0, await d.RunBatchAsync());
        Assert.Equal(["a.bin", "b.bin", "c.bin"], d.Batch.Files.Select(f => f.Name));
        Assert.Equal([[1, 3], [2, 5], [4]], d.Batch.Files.Select(f => f.Links.Select(m => m.Index).ToArray()));
        Assert.All(d.Batch.Files, f => Assert.Equal(FileState.Done, f.State));
        Assert.Equal(A, File.ReadAllBytes(d.PathOf("a.bin")));
        Assert.Equal(B, File.ReadAllBytes(d.PathOf("b.bin")));
        Assert.Equal(C, File.ReadAllBytes(d.PathOf("c.bin")));
        Assert.Equal(["a.bin", "b.bin", "c.bin"], d.Files);
        Assert.Contains("lead to 3 files", d.Output);
        Assert.Contains("3 files: 3 saved", d.Output);
        Assert.Equal(0, d.Server.Open);
    }

    [Fact]
    public async Task Parts_of_the_same_size_under_other_names_stay_files_of_their_own()
    {
        // As the parts of a split archive: all of one size, each with its own content.
        using var d = new Download();
        var part2 = Download.Bytes(Size, 22);
        d.Link(A, "movie.part1.rar");
        d.Link(part2, "movie.part2.rar");
        d.Link(A, "movie.part1.rar");
        d.Link(part2, "movie.part2.rar");

        Assert.Equal(0, await d.RunBatchAsync());
        Assert.Equal(["movie.part1.rar", "movie.part2.rar"], d.Files);
        Assert.Equal(A, File.ReadAllBytes(d.PathOf("movie.part1.rar")));
        Assert.Equal(part2, File.ReadAllBytes(d.PathOf("movie.part2.rar")));
    }

    [Fact]
    public async Task Links_with_other_names_but_the_same_content_are_mirrors_of_one_file()
    {
        using var d = new Download();
        d.Link(A, "setup-1.2.exe");
        d.Link(A, "setup.exe");

        Assert.Equal(0, await d.RunBatchAsync());
        var file = Assert.Single(d.Batch.Files);
        Assert.Equal(2, file.Links.Count);
        Assert.Equal(["setup-1.2.exe"], d.Files);
        Assert.All(file.Links, m => Assert.True(m.Bytes > 0)); // both were used
    }

    [Fact]
    public async Task Links_without_a_file_name_are_told_apart_by_content_and_get_numbered_names()
    {
        using var d = new Download();
        var other = Download.Bytes(Size, 23);
        d.Link(A, "download");
        d.Link(other, "download");
        d.Link(A, "download");

        Assert.Equal(0, await d.RunBatchAsync());
        Assert.Equal(["download", "download (1)"], d.Batch.Files.Select(f => f.Name));
        Assert.Equal(A, File.ReadAllBytes(d.PathOf("download")));
        Assert.Equal(other, File.ReadAllBytes(d.PathOf("download (1)")));
    }

    [Fact]
    public async Task Links_with_the_same_name_are_one_file_and_one_with_another_size_is_skipped()
    {
        using var d = new Download();
        d.Link(A, "a.bin");
        d.Link(A, "a.bin");
        d.Link(B, "a.bin"); // an old version on a mirror that wasn't updated

        Assert.Equal(0, await d.RunBatchAsync());
        var file = Assert.Single(d.Batch.Files);
        Assert.Contains("different size", file.Links[2].Problem);
        Assert.Equal(A, File.ReadAllBytes(d.PathOf("a.bin")));
    }

    [Fact]
    public async Task A_link_that_doesnt_work_fails_its_own_file_and_the_others_are_downloaded()
    {
        using var d = new Download();
        d.Link(A, "a.bin");
        d.Job.Urls.Add(new Uri("http://gone.test/b.zip"));      // 404, a file of its own
        d.Job.Urls.Add(new Uri("http://gone.test/a.bin"));      // 404, but a mirror of a.bin by its name

        Assert.Equal(1, await d.RunBatchAsync());
        Assert.Equal(["a.bin", "b.zip"], d.Batch.Files.Select(f => f.Name));
        Assert.Equal([1, 3], d.Batch.Files[0].Links.Select(m => m.Index));
        Assert.Equal(FileState.Done, d.Batch.Files[0].State);
        Assert.Equal(FileState.Failed, d.Batch.Files[1].State);
        Assert.Equal("None of the links work.", d.Batch.Files[1].Message);
        Assert.Equal(A, File.ReadAllBytes(d.PathOf("a.bin")));
    }

    [Fact]
    public async Task Checksums_go_with_their_file_by_name_or_link_and_the_rest_are_matched_by_computing()
    {
        using var d = new Download();
        d.Link(A, "a.bin");
        d.Link(B, "b.bin");
        var b = d.Job.Urls[^1];
        d.Link(C, "c.bin");
        d.Link(Download.Bytes(Size + 21, 14), "d.bin");
        Input.Add($"{Download.Sha256(A)}  a.bin", d.Job, []);              // sha256sum's way
        Input.Add($"{b} sha256:{Download.Sha256(B)}", d.Job, []);           // on the line of the link
        Input.Add(Download.Sha256(C), d.Job, []);                           // for one of the files, not said which
        Input.Add(Download.Sha256(Download.Bytes(99, 99)), d.Job, []);      // for none of them

        Assert.Equal(0, await d.RunBatchAsync());
        var files = d.Batch.Files;
        Assert.Equal([Download.Sha256(A)], files[0].Job.Checksums);
        Assert.False(files[0].Job.ChecksumsOfSeveral);
        Assert.Equal([Download.Sha256(B)], files[1].Job.Checksums);
        Assert.True(files[2].Job.ChecksumsOfSeveral);
        Assert.Equal(Download.Sha256(C), files[2].ChecksumMatched);
        Assert.Null(files[3].ChecksumMatched);
        Assert.Contains("[4] The file matches none of the 2 checksums pasted", d.Output);
        Assert.Contains($"No file matches the checksum {Download.Sha256(Download.Bytes(99, 99))}", d.Output);
        Assert.All(files, f => Assert.Equal(FileState.Done, f.State));
    }

    [Fact]
    public async Task A_checksum_that_doesnt_match_its_file_marks_only_that_file()
    {
        using var d = new Download();
        d.Link(A, "a.bin");
        d.Link(B, "b.bin");
        Input.Add($"SHA256 (a.bin) = {Download.Sha256(B)}", d.Job, []); // BSD's way, but the wrong one

        Assert.Equal(2, await d.RunBatchAsync());
        Assert.Equal(FileState.Mismatch, d.Batch.Files[0].State);
        Assert.Equal(FileState.Done, d.Batch.Files[1].State);
        Assert.Contains("[1] SHA256 does NOT match", d.Output);
    }

    [Fact]
    public async Task A_checksum_said_for_no_file_is_matched_also_when_only_one_file_has_none_of_its_own()
    {
        // A page lists a.bin's SHA-256 with its name and, below it, a.bin's MD5 without: that is no checksum of b.bin.
        using var d = new Download();
        d.Link(A, "a.bin");
        d.Link(B, "b.bin");
        Input.Add($"{Download.Sha256(A)}  a.bin", d.Job, []);
        d.Job.Checksums.Add(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(A)));

        Assert.Equal(0, await d.RunBatchAsync());
        Assert.True(d.Batch.Files[1].Job.ChecksumsOfSeveral);
        Assert.Equal([FileState.Done, FileState.Done], d.Batch.Files.Select(f => f.State));
        Assert.Contains("[2] The file matches none of the 1 checksum pasted", d.Output);
    }

    [Fact]
    public async Task A_checksum_left_over_when_every_file_has_its_own_is_said_not_dropped()
    {
        using var d = new Download();
        d.Link(A, "a.bin");
        d.Link(B, "b.bin");
        Input.Add($"{Download.Sha256(A)}  a.bin", d.Job, []);
        Input.Add($"{Download.Sha256(B)}  b.bin", d.Job, []);
        string extra = Download.Sha256(C);
        d.Job.Checksums.Add(extra);

        Assert.Equal(0, await d.RunBatchAsync());
        Assert.Contains($"The checksum {extra} goes with none of the files", d.Output);
    }

    [Fact]
    public async Task One_file_with_a_checksum_not_named_is_checked_against_it_as_before()
    {
        using var d = new Download();
        d.Link(A, "a.bin");
        d.Job.Checksums.Add(Download.Sha256(B));

        Assert.Equal(2, await d.RunBatchAsync());
        Assert.False(d.Batch.Files[0].Job.ChecksumsOfSeveral);
        Assert.Contains("SHA256 does NOT match", d.Output);
    }

    [Fact]
    public async Task A_finished_file_in_the_save_folder_is_only_checked_not_downloaded_again()
    {
        using var d = new Download();
        File.WriteAllBytes(d.PathOf("a.bin"), A);
        d.Link(A, "a.bin");
        d.Link(A, "a.bin");

        Assert.Equal(0, await d.RunBatchAsync());
        var file = Assert.Single(d.Batch.Files);
        Assert.Equal(FileState.Checked, file.State);
        Assert.True(file.CheckedOnly);
        Assert.Equal(["a.bin"], d.Files);
        Assert.Equal(0, file.Links.Sum(m => m.Bytes)); // nothing downloaded
        Assert.Contains("complete already: it is only checked", d.Output);
    }

    [Fact]
    public async Task Another_file_of_the_same_size_under_the_name_stays_and_the_download_gets_a_number()
    {
        using var d = new Download();
        var mine = Download.Bytes(Size, 31);
        File.WriteAllBytes(d.PathOf("a.bin"), mine);
        d.Link(A, "a.bin");

        Assert.Equal(0, await d.RunBatchAsync());
        Assert.Equal(mine, File.ReadAllBytes(d.PathOf("a.bin")));
        Assert.Equal(A, File.ReadAllBytes(d.PathOf("a (1).bin")));
        Assert.Contains("another file or version; it stays as it is", d.Output);
    }

    [Fact]
    public async Task An_unfinished_file_given_goes_with_the_file_of_its_name()
    {
        using var d = new Download();
        string part = d.PathOf("b.bin.part");
        File.WriteAllBytes(part, B.AsSpan(0, Size / 2).ToArray());
        File.WriteAllText(part + ".mirrordl", $"mirrordl 2\nsize {B.Length}\nmissing {Size / 2} {B.Length}\nend\n");
        d.Link(A, "a.bin");
        d.Link(B, "b.bin");
        d.Job.PartFiles.Add(part);

        Assert.Equal(0, await d.RunBatchAsync());
        Assert.Empty(d.Batch.Files[0].Job.PartFiles);
        Assert.Equal([part], d.Batch.Files[1].Job.PartFiles);
        Assert.Equal(B, File.ReadAllBytes(d.PathOf("b.bin")));
        Assert.Equal(B.Length - Size / 2, d.Batch.Files[1].Links.Sum(m => m.Bytes)); // only the rest
    }

    [Fact]
    public async Task Unfinished_Firefox_files_of_several_files_each_go_on_with_their_own_file()
    {
        using var d = new Download();
        byte[] movie1 = Download.Bytes(6 << 20, 41), movie2 = Download.Bytes((5 << 20) + 77, 42);
        // Firefox stopped two downloads: one with its random piece in the name and the empty placeholder next to it
        // (found in the save folder by itself), one plain .part file added by the user. A third file is new.
        File.WriteAllBytes(d.PathOf("movie.part1.rar"), []);
        File.WriteAllBytes(d.PathOf("movie.part1.Xy12_-Ab.rar.part"), movie1[..(4 << 20)]);
        File.WriteAllBytes(d.PathOf("movie.part2.rar.part"), movie2[..(3 << 20)]);
        d.Link(movie1, "movie.part1.rar");
        d.Link(movie2, "movie.part2.rar");
        d.Link(movie1, "movie.part1.rar");
        d.Link(C, "movie.part3.rar");
        d.Job.PartFiles.Add(d.PathOf("movie.part2.rar.part"));

        Assert.Equal(0, await d.RunBatchAsync());
        Assert.Equal(["movie.part1.rar", "movie.part2.rar", "movie.part3.rar"], d.Files); // placeholder taken, no .part left
        Assert.Equal(movie1, File.ReadAllBytes(d.PathOf("movie.part1.rar")));
        Assert.Equal(movie2, File.ReadAllBytes(d.PathOf("movie.part2.rar")));
        Assert.Equal(C, File.ReadAllBytes(d.PathOf("movie.part3.rar")));
        // Only the rest was downloaded: Firefox's last MB, which may not have reached the disk, is fetched again.
        Assert.Equal(movie1.Length - (3 << 20), d.Batch.Files[0].Links.Sum(m => m.Bytes));
        Assert.Equal(movie2.Length - (2 << 20), d.Batch.Files[1].Links.Sum(m => m.Bytes));
        Assert.Equal(C.Length, d.Batch.Files[2].Links.Sum(m => m.Bytes));
        Assert.Contains("Found movie.part1.Xy12_-Ab.rar.part", d.Output);
        Assert.Contains("Found movie.part2.rar.part", d.Output);
    }

    [Fact]
    public async Task An_unfinished_file_that_fits_no_file_is_left_out_and_said()
    {
        using var d = new Download();
        string mine = d.PathOf("something else.dat.part");
        File.WriteAllBytes(mine, new byte[1000]);
        d.Link(A, "a.bin");
        d.Link(B, "b.bin");
        d.Job.PartFiles.Add(mine);

        Assert.Equal(0, await d.RunBatchAsync());
        Assert.Contains("it can't be told which of the files it is an unfinished download of", d.Output);
        Assert.True(File.Exists(mine));
    }

    [Fact]
    public async Task Pausing_stops_at_its_file_and_the_next_run_goes_on_there()
    {
        using var d = new Download();
        foreach (var link in new[] { d.Link(A, "a.bin"), d.Link(B, "b.bin") })
            link.ChunkDelay = TimeSpan.FromMilliseconds(15);
        using var pause = new CancellationTokenSource();
        d.OnPhase = phase =>
        {
            if (phase != Phase.Downloading || d.Batch.Current?.Number != 1) return;
            var file = d.Batch.Current;
            _ = Task.Run(async () =>
            {
                while (file.Links.Sum(m => m.Bytes) < 1 << 20 && file.State == FileState.Running)
                    await Task.Delay(10, CancellationToken.None);
                pause.Cancel();
            }, CancellationToken.None);
        };

        Assert.Equal(130, await d.RunBatchAsync(pause));
        Assert.Equal([FileState.Paused, FileState.Waiting], d.Batch.Files.Select(f => f.State));
        Assert.Contains("The progress is saved", d.Batch.Files[0].Message);
        long before = d.Batch.Files[0].Links.Sum(m => m.Bytes);

        d.OnPhase = null;
        Assert.Equal(0, await d.RunBatchAsync(again: true));
        Assert.Equal([FileState.Done, FileState.Done], d.Batch.Files.Select(f => f.State));
        Assert.Equal(A, File.ReadAllBytes(d.PathOf("a.bin")));
        Assert.Equal(B, File.ReadAllBytes(d.PathOf("b.bin")));
        Assert.Equal(Size - before, d.Batch.Files[0].Links.Sum(m => m.Bytes)); // new links for the second run: only the rest
    }

    [Fact]
    public async Task A_saved_batch_goes_on_after_a_restart_and_keeps_what_was_done()
    {
        using var d = new Download();
        d.Link(A, "a.bin");
        d.Link(B, "b.bin").ChunkDelay = TimeSpan.FromMilliseconds(15);
        using var pause = new CancellationTokenSource();
        d.OnPhase = phase =>
        {
            if (phase != Phase.Downloading || d.Batch.Current?.Number != 2) return;
            var file = d.Batch.Current;
            _ = Task.Run(async () =>
            {
                while (file.Links.Sum(m => m.Bytes) < 1 << 19 && file.State == FileState.Running)
                    await Task.Delay(10, CancellationToken.None);
                pause.Cancel();
            }, CancellationToken.None);
        };
        Assert.Equal(130, await d.RunBatchAsync(pause));

        // Kept as MirrorDL keeps it, and read back as after a restart.
        string json = JsonSerializer.Serialize(d.Batch.ToSaved());
        var saved = JsonSerializer.Deserialize<SavedBatch>(json)!;
        d.OnPhase = null;
        Assert.Equal(0, await d.RunBatchAsync(saved: saved));
        Assert.Equal([FileState.Done, FileState.Done], d.Batch.Files.Select(f => f.State));
        Assert.Equal(0, d.Batch.Files[0].Links.Sum(m => m.Bytes)); // done before: not even asked again
        Assert.Equal(B, File.ReadAllBytes(d.PathOf("b.bin")));
        Assert.Contains("Found b.bin.part (mirrordl progress)", d.Output);
        Assert.True(saved.Files![1].Have > 0); // how far it got, for the list after the restart
    }

    [Fact]
    public async Task A_removed_file_is_not_downloaded()
    {
        using var d = new Download();
        d.Link(A, "a.bin");
        d.Link(B, "b.bin");
        d.OnPhase = phase =>
        {
            if (phase == Phase.Checking && d.Batch.Current?.Number == 1) Assert.True(d.Batch.Remove(d.Batch.Files[1]));
        };

        Assert.Equal(0, await d.RunBatchAsync());
        Assert.Equal(FileState.Removed, d.Batch.Files[1].State);
        Assert.Equal(["a.bin"], d.Files);
    }

    [Fact]
    public async Task The_file_being_downloaded_cant_be_removed()
    {
        using var d = new Download();
        d.Link(A, "a.bin").ChunkDelay = TimeSpan.FromMilliseconds(5);
        bool? removed = null;
        d.OnPhase = phase =>
        {
            if (phase == Phase.Downloading) removed ??= d.Batch.Remove(d.Batch.Files[0]);
        };

        Assert.Equal(0, await d.RunBatchAsync());
        Assert.False(removed);
        Assert.Equal(FileState.Done, d.Batch.Files[0].State);
    }

    [Fact]
    public async Task Cancelled_while_the_links_are_checked_the_next_run_checks_them_anew()
    {
        using var d = new Download();
        d.Link(A, "a.bin").Answer = (_, n) =>
        {
            if (n == 1) Thread.Sleep(300); // slow to answer the first time, so that the check can be cancelled
            return null;
        };
        using var pause = new CancellationTokenSource(100);

        Assert.Equal(130, await d.RunBatchAsync(pause));
        Assert.False(d.Batch.IsSorted);
        Assert.Contains("Cancelled.", d.Output);
        Assert.Equal(0, await d.RunBatchAsync(again: true));
        Assert.Equal(A, File.ReadAllBytes(d.PathOf("a.bin")));
    }

    [Fact]
    public async Task With_pages_not_searched_a_page_is_said_to_be_one()
    {
        using var d = new Download();
        d.Job.LookIntoPages = false;
        var page = d.Link(System.Text.Encoding.UTF8.GetBytes("""<a href="http://cdn.test/x.rar">x.rar</a>"""), "f/abc");
        page.ContentType = "text/html";

        Assert.Equal(1, await d.RunBatchAsync());
        Assert.True(d.Batch.Files[0].IsPage);
        Assert.Contains("pages aren't searched for links", d.Batch.Files[0].Message);
        Assert.Equal(1, page.Requests); // asked what it is, never read
    }

    [Fact]
    public void The_summary_counts_each_way_a_file_ended()
    {
        var files = new[] { FileState.Done, FileState.Done, FileState.Mismatch, FileState.Paused }
            .Select((state, i) => new BatchFile(i + 1, $"f{i}", 10, new Job(), []) { State = state }).ToList();
        Assert.Equal("4 files: 2 saved, 1 doesn't match, 1 paused", Batch.Summary(files));
    }

    [Fact]
    public async Task With_one_file_set_all_links_are_mirrors_of_it_as_before()
    {
        using var d = new Download();
        d.Job.OneFile = true;
        d.Link(A, "a.bin");
        d.Link(B, "b.bin");

        Assert.Equal(0, await d.RunBatchAsync());
        var file = Assert.Single(d.Batch.Files);
        Assert.Contains("different size", file.Links[1].Problem);
        Assert.Equal(["a.bin"], d.Files);
    }

    [Fact]
    public async Task Two_batches_with_a_file_of_the_same_name_in_one_folder_dont_share_it()
    {
        using var first = new Download();
        using var second = new Download();
        second.Job.OutDir = first.Folder.Path;
        first.Link(A, "download");
        var other = second.Server.Add("http://elsewhere.test/download", B);
        second.Job.Urls.Add(new Uri("http://elsewhere.test/download"));

        var batch = new Batch(first.Job) { Handler = first.Server };
        try
        {
            // The first is sorted (its names claimed) but not done, as while it waits or is paused.
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
            {
                batch.Sorted += () => stop.Cancel();
                Assert.Equal(130, await batch.RunAsync(stop.Token));
            }
            Assert.Equal(0, await second.RunBatchAsync());
            Assert.Equal("download (1)", second.Batch.Files[0].Name);
            Assert.Contains("download", second.Batch.Files[0].Job.OtherNames);
            Assert.Equal(B, File.ReadAllBytes(first.PathOf("download (1)")));
            Assert.True(other.Requests > 0);
        }
        finally
        {
            batch.Release();
        }
    }
}
