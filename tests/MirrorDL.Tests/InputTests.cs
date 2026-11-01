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

        // All five algorithms, with the checksums of "abc" from the standards (RFC 1321, FIPS 180).
        var results = await Checksum.ComputeAsync(path,
            [Sha256OfAbc, "900150983cd24fb0d6963f7d28e17f72", "a9993e364706816aba3e25717850c26c9cd0d89d",
             "cb00753f45a35e8bb5a03d699ac65007272c32ab0eded1631a8b605a43ff5bed8086072ba1e7cc2358baeca134c825a7",
             "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f"],
            (done, _) => lastDone = done, TestContext.Current.CancellationToken);

        Assert.Equal(["SHA256", "MD5", "SHA1", "SHA384", "SHA512"], results.Select(r => r.Algorithm));
        Assert.All(results, r => Assert.Equal(r.Expected, r.Actual));
        Assert.Equal(3, lastDone);
    }

    [Fact]
    public async Task ComputeAsync_compares_several_checksums_of_one_algorithm_with_the_same_result()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "abc.txt");
        File.WriteAllText(path, "abc");
        string other = new('0', 64);

        var results = await Checksum.ComputeAsync(path, [other, Sha256OfAbc], (_, _) => { }, TestContext.Current.CancellationToken);

        Assert.Equal([(other, Sha256OfAbc), (Sha256OfAbc, Sha256OfAbc)], results.Select(r => (r.Expected, r.Actual)));
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

    [Theory]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  movie.part1.rar")]   // sha256sum
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad *movie.part1.rar")]   // sha256sum -b
    [InlineData("SHA256 (movie.part1.rar) = ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")] // BSD
    [InlineData("movie.part1.rar: ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("./files/movie.part1.rar ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public void A_checksum_with_a_file_name_on_its_line_is_that_files(string line)
    {
        var job = new Job();
        Input.Add(line, job, []);
        Assert.Empty(job.Checksums);
        Assert.Equal([new FileChecksum("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "movie.part1.rar")], job.FileChecksums);
    }

    [Theory]
    [InlineData("SHA256: ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("sha-256 ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public void A_checksum_with_only_its_algorithm_belongs_to_no_file_in_particular(string line)
    {
        var job = new Job();
        Input.Add(line, job, []);
        Assert.Equal(["ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"], job.Checksums);
        Assert.Empty(job.FileChecksums);
    }

    [Fact]
    public void A_checksum_on_the_line_of_a_link_is_that_links_files()
    {
        var job = new Job();
        Input.Add("https://a.example/x.iso sha256:BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", job, []);
        Assert.Equal([new Uri("https://a.example/x.iso")], job.Urls);
        Assert.Empty(job.Checksums);
        Assert.Equal([new FileChecksum("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Link: new Uri("https://a.example/x.iso"))],
            job.FileChecksums);
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
