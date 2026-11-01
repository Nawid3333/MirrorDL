// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

namespace MirrorDL.Tests;

/// <summary>
/// Files the user gives: a finished one is only checked - never written, renamed or moved - and one under a name
/// of its own keeps that name. The finished files are read-only during the run, so any write fails the test.
/// </summary>
public class SessionCheckTests
{
    const int MB = 1 << 20;
    const int Size = 3 * MB + 12_345;
    static readonly byte[] File1 = Download.Bytes(Size, 1);

    static string Given(Download d, string name, byte[] content, bool readOnly = true)
    {
        string path = d.PathOf(name);
        File.WriteAllBytes(path, content);
        if (readOnly) File.SetAttributes(path, FileAttributes.ReadOnly);
        d.Job.PartFiles.Add(path);
        return path;
    }

    [Fact]
    public async Task A_finished_file_is_only_checked_and_keeps_its_name_whatever_the_link_is_called()
    {
        // As on 2026-11-01: the link's only name was a token at the end of its address.
        using var d = new Download();
        string game = Given(d, "game.rar", File1);
        var written = File.GetLastWriteTimeUtc(game);
        d.Link(File1, "ISYQvAPiMv");
        d.Link(File1, "ISYQvAPiMv");

        Assert.Equal(0, await d.RunAsync());
        Assert.True(d.Session.CheckedOnly);
        Assert.False(d.Session.SizeOnly);
        Assert.Equal(["game.rar"], d.Files);
        Assert.Equal(File1, File.ReadAllBytes(game));
        Assert.Equal(written, File.GetLastWriteTimeUtc(game));
        Assert.Equal(game, d.Session.SavedPath);
        Assert.Equal(0, d.Session.Mirrors.Sum(m => m.Bytes));
        Assert.Contains("only checked", d.Output);
        Assert.Contains("Checked: the file is complete and matches", d.Output);
        Assert.DoesNotContain("Downloaded", d.Output);
    }

    [Fact]
    public async Task A_finished_file_that_misses_the_checksum_is_said_so_and_left_as_it_was()
    {
        using var d = new Download();
        string game = Given(d, "game.rar", File1);
        d.Link(File1);
        d.Job.Checksums.Add(Download.Sha256([1, 2, 3]));

        Assert.Equal(2, await d.RunAsync());
        Assert.True(d.Session.CheckedOnly);
        Assert.Equal(File1, File.ReadAllBytes(game));
        Assert.Contains("The file doesn't match the checksum: either it is damaged, or the checksum is for another file", d.Output);
    }

    [Fact]
    public async Task A_finished_file_of_another_file_is_refused_and_left_as_it_was()
    {
        using var d = new Download();
        byte[] other = Download.Bytes(Size, 2);
        string game = Given(d, "game.rar", other);
        d.Link(File1);

        Assert.Equal(1, await d.RunAsync());
        Assert.Contains("is not the same file as the links", d.Session.ErrorMessage);
        Assert.Contains("stays as it is", d.Session.ErrorMessage); // no advice to delete the user's file
        Assert.Equal(other, File.ReadAllBytes(game));
        Assert.Equal(["game.rar"], d.Files);
    }

    [Fact]
    public async Task Without_range_requests_a_finished_file_is_checked_by_its_checksum()
    {
        using var d = new Download();
        string game = Given(d, "game.rar", File1);
        d.Link(File1).Ranges = false;
        d.Job.Checksums.Add(Download.Sha256(File1));

        Assert.Equal(0, await d.RunAsync());
        Assert.True(d.Session.CheckedOnly);
        Assert.False(d.Session.SizeOnly);
        Assert.Equal(["game.rar"], d.Files);
        Assert.Contains("SHA256 matches", d.Output);
        Assert.DoesNotContain("can't send parts", d.Output); // nothing is downloaded, so that isn't said
    }

    [Fact]
    public async Task Without_range_requests_and_a_checksum_only_the_size_can_be_checked_and_that_is_said()
    {
        using var d = new Download();
        Given(d, "game.rar", File1);
        d.Link(File1).Ranges = false;

        Assert.Equal(0, await d.RunAsync());
        Assert.True(d.Session.SizeOnly);
        Assert.Contains("nothing to compare its content with", d.Output);
        Assert.Equal(["game.rar"], d.Files);
    }

    [Fact]
    public async Task An_empty_finished_file_is_only_checked_too()
    {
        using var d = new Download();
        Given(d, "notes.dat", []);
        d.Link([]);

        Assert.Equal(0, await d.RunAsync());
        Assert.True(d.Session.CheckedOnly);
        Assert.False(d.Session.SizeOnly); // for an empty file, the size is all there is
        Assert.Equal(["notes.dat"], d.Files);
    }

    [Fact]
    public async Task An_unfinished_file_under_a_name_of_its_own_is_continued_in_place()
    {
        using var d = new Download();
        string copy = Given(d, "my copy.dat", File1[..(2 * MB)], readOnly: false);
        d.Link(File1);

        Assert.Equal(0, await d.RunAsync());
        Assert.False(d.Session.CheckedOnly);
        Assert.Equal(["my copy.dat"], d.Files);
        Assert.Equal(File1, File.ReadAllBytes(copy));
    }

    [Fact]
    public async Task Without_range_requests_an_unfinished_file_of_the_users_stays_and_a_new_one_is_downloaded()
    {
        using var d = new Download();
        byte[] head = File1[..MB];
        string copy = Given(d, "my copy.dat", head);
        d.Link(File1).Ranges = false;

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(head, File.ReadAllBytes(copy));
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Contains("my copy.dat stays as it is; the file is downloaded anew", d.Output);
        Assert.Empty(d.Questions);
    }

    [Fact]
    public async Task A_finished_file_is_checked_and_a_matching_part_file_next_to_it_goes_to_the_Recycle_Bin()
    {
        using var d = new Download();
        string game = Given(d, "data.bin", File1);
        File.WriteAllBytes(d.PathOf("data.Ab12Cd34.bin.part"), File1[..(2 * MB)]);
        d.Link(File1);

        Assert.Equal(0, await d.RunAsync());
        Assert.True(d.Session.CheckedOnly);
        Assert.Equal(File1, File.ReadAllBytes(game));
        Assert.Equal(["data.Ab12Cd34.bin.part"], d.Recycled);
    }
}
