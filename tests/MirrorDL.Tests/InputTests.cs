// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

namespace MirrorDL.Tests;

public class ChecksumTests
{
    const string Sha256OfAbc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Theory]
    [InlineData(Sha256OfAbc, Sha256OfAbc)]
    [InlineData("SHA256:" + "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", Sha256OfAbc)]
    [InlineData("md5:900150983cd24fb0d6963f7d28e17f72", "900150983cd24fb0d6963f7d28e17f72")]
    public void TryParse_accepts_plain_and_prefixed_checksums(string token, string expected)
    {
        Assert.True(Checksum.TryParse(token, out string? hex));
        Assert.Equal(expected, hex);
    }

    [Theory]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015a")] // 63 characters
    [InlineData("zz7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")] // not hex
    [InlineData("SHA256:")]
    public void TryParse_rejects_what_is_not_a_checksum(string token) => Assert.False(Checksum.TryParse(token, out _));

    [Fact]
    public async Task ComputeAsync_reads_the_file_once_for_all_algorithms_and_reports_progress()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "abc.txt");
        File.WriteAllText(path, "abc");
        long lastDone = -1;

        var results = await Checksum.ComputeAsync(path, [Sha256OfAbc, "900150983cd24fb0d6963f7d28e17f72"],
            (done, _) => lastDone = done, TestContext.Current.CancellationToken);

        Assert.All(results, r => Assert.Equal(r.Expected, r.Actual));
        Assert.Equal(3, lastDone);
    }
}

public class InputTests
{
    [Fact]
    public void Links_checksums_and_junk_are_told_apart()
    {
        var job = new Job();
        var ignored = new List<string>();
        Input.Add("https://a.example/file.iso http://b.example/file.iso", job, ignored);
        Input.Add("https://a.example/file.iso", job, ignored); // the same link again
        Input.Add("SHA256: ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", job, ignored);
        Input.Add("some words from the page", job, ignored);

        Assert.Equal(2, job.Urls.Count);
        Assert.Single(job.Checksums);
        Assert.Contains("words", ignored);
    }

    [Fact]
    public void A_dragged_in_part_file_is_the_one_to_continue()
    {
        using var folder = new TempFolder();
        string part = folder.File("my file.Ab12Cd34.bin.part", 10);
        var job = new Job();
        Input.Add($"\"{part}\"", job, []);
        Assert.Equal([part], job.PartFiles);
    }

    [Fact]
    public void A_dragged_in_aria2_progress_file_means_the_download_next_to_it()
    {
        using var folder = new TempFolder();
        string download = folder.File("big.iso", 10);
        string control = folder.File("big.iso.aria2", 10);
        var job = new Job();
        Input.Add(control, job, []);
        Assert.Equal([download], job.PartFiles);
    }

    [Fact]
    public void A_folder_is_where_to_save_and_a_txt_file_holds_links()
    {
        using var folder = new TempFolder();
        string list = Path.Combine(folder.Path, "links.txt");
        File.WriteAllLines(list, ["https://a.example/x.zip", "", "https://b.example/x.zip"]);
        var job = new Job();
        Input.Add(folder.Path, job, []);
        Input.Add(list, job, []);
        Assert.Equal(folder.Path, job.OutDir);
        Assert.Equal(2, job.Urls.Count);
    }

    [Fact]
    public void Any_other_dragged_in_file_is_an_unfinished_download_whatever_its_name_and_each_counts_once()
    {
        using var folder = new TempFolder();
        string renamed = folder.File("my copy.bin", 10);
        string part = folder.File("other.Ab12Cd34.bin.part", 10);
        var job = new Job();
        Input.Add(renamed, job, []);
        Input.Add($"\"{part}\"", job, []);
        Input.Add(renamed, job, []);
        Assert.Equal([renamed, part], job.PartFiles);
    }
}
