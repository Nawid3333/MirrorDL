// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

namespace MirrorDL.Tests;

/// <summary>Continuing unfinished files of MirrorDL, Firefox and aria2, and putting several together.</summary>
public class SessionUnfinishedTests
{
    const int MB = 1 << 20;
    const int Size = 3 * MB + 12_345;
    static readonly byte[] File1 = Download.Bytes(Size, 1);

    /// <summary>A file of <paramref name="length"/> bytes holding <paramref name="data"/> at the given ranges, zeros elsewhere.</summary>
    static byte[] Holding(int length, params (int Start, int End)[] ranges)
    {
        var bytes = new byte[length];
        foreach (var (start, end) in ranges) Array.Copy(File1, start, bytes, start, end - start);
        return bytes;
    }

    [Fact]
    public async Task Continues_its_own_unfinished_file_and_downloads_only_what_is_missing()
    {
        using var d = new Download();
        File.WriteAllBytes(d.PathOf("data.bin.part"), Holding(Size, (MB, 2 * MB)));
        new PartState(Size, [(0, MB), (2 * MB, Size)]).Save(d.PathOf("data.bin.part.mirrordl"));
        d.Link(File1);

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Equal(["data.bin"], d.Files);
        Assert.Equal(Size - MB, d.Session.Mirrors.Sum(m => m.Bytes));
        Assert.Contains("Found data.bin.part (mirrordl progress)", d.Output);
    }

    [Fact]
    public async Task Continues_Firefox_part_file_and_takes_the_place_of_its_empty_placeholder()
    {
        using var d = new Download();
        File.WriteAllBytes(d.PathOf("data.Ab12Cd34.bin.part"), File1[..(2 * MB)]);
        File.WriteAllBytes(d.PathOf("data.bin"), []);
        d.Link(File1);

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(["data.bin"], d.Files);
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        // Firefox may not have written its last bytes to the disk yet: the last MB is downloaded again.
        Assert.Equal(Size - MB, d.Session.Mirrors.Sum(m => m.Bytes));
    }

    [Fact]
    public async Task Keeps_the_good_beginning_of_a_Firefox_file_whose_end_never_reached_the_disk()
    {
        using var d = new Download();
        File.WriteAllBytes(d.PathOf("data.Ab12Cd34.bin.part"), Holding(3 * MB, (0, 3 * MB / 2))); // then zeros
        File.WriteAllBytes(d.PathOf("data.bin"), []);
        d.Link(File1);

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Contains("never reached the disk", d.Output);
    }

    [Fact]
    public async Task Continues_a_file_aria2_started_and_keeps_its_name_and_place()
    {
        using var d = new Download();
        File.WriteAllBytes(d.PathOf("data.bin"), Holding(Size, (0, MB), (2 * MB, 3 * MB)));
        File.WriteAllBytes(d.PathOf("data.bin.aria2"), Aria2ControlTests.ControlFile(true, Size, 0b1010_0000));
        d.Link(File1);

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Equal(["data.bin"], d.Files); // the .aria2 file is gone, as aria2 removes it at the end
        Assert.Contains("(aria2 progress)", d.Output);
    }

    [Fact]
    public async Task Finds_its_own_progress_next_to_a_file_under_the_final_name()
    {
        // A file continued under the final name (aria2's), paused: the next start finds it by its .mirrordl file.
        using var d = new Download();
        File.WriteAllBytes(d.PathOf("data.bin"), Holding(Size, (0, MB)));
        new PartState(Size, [(MB, Size)]).Save(d.PathOf("data.bin.mirrordl"));
        d.Link(File1);

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(["data.bin"], d.Files);
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Equal(Size - MB, d.Session.Mirrors.Sum(m => m.Bytes));
    }

    /// <summary>MirrorDL's own file with the second MB, and a numbered Firefox copy with the first two.</summary>
    static void TwoUnfinished(Download d)
    {
        File.WriteAllBytes(d.PathOf("data.bin.part"), Holding(Size, (MB, 2 * MB)));
        new PartState(Size, [(0, MB), (2 * MB, Size)]).Save(d.PathOf("data.bin.part.mirrordl"));
        File.WriteAllBytes(d.PathOf("data(1).Ab12Cd34.bin.part"), File1[..(2 * MB)]); // holds the first MB for sure
    }

    [Fact]
    public async Task Puts_several_unfinished_files_together_and_moves_the_spare_to_the_Recycle_Bin()
    {
        using var d = new Download();
        TwoUnfinished(d);
        d.Link(File1);

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
        Assert.Contains("Took 1.00 MB from data(1).Ab12Cd34.bin.part", d.Output);
        Assert.Equal(Size - 2 * MB, d.Session.Mirrors.Sum(m => m.Bytes));
        Assert.Equal(["data(1).Ab12Cd34.bin.part"], d.Recycled);
        Assert.Equal(["data.bin"], d.Files);
        Assert.Contains("Moved the other unfinished files", d.Output);
    }

    [Fact]
    public async Task Keeps_the_spare_when_the_finished_file_doesnt_match_the_checksum()
    {
        using var d = new Download();
        TwoUnfinished(d);
        d.Link(File1);
        d.Job.Checksums.Add(Download.Sha256([1, 2, 3]));

        Assert.Equal(2, await d.RunAsync());
        Assert.Empty(d.Recycled);
        Assert.True(File.Exists(d.PathOf("data(1).Ab12Cd34.bin.part")));
        Assert.Contains("are kept, since the finished file doesn't match the checksum", d.Output);
    }

    [Theory]
    [InlineData(false, false, true)]  // asked, the answer is no: kept
    [InlineData(true, false, false)]  // asked, the answer is yes: deleted
    [InlineData(false, true, false)]  // the option: deleted without asking
    public async Task A_spare_that_cant_go_to_the_Recycle_Bin_is_deleted_only_when_the_user_says_so(bool answer, bool option, bool kept)
    {
        using var d = new Download { RecycleResult = Native.Recycled.NoRoom, Answer = answer };
        TwoUnfinished(d);
        d.Link(File1);
        d.Job.DeleteLeftovers = option;

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin"))); // the finished file, whatever happens
        Assert.Equal(kept, File.Exists(d.PathOf("data(1).Ab12Cd34.bin.part")));
        Assert.Equal(option ? 0 : 1, d.Questions.Count);
        Assert.Contains(kept ? "Kept, since they can't go to the Recycle Bin" : "Deleted for good: data(1).Ab12Cd34.bin.part", d.Output);
    }

    [Fact]
    public async Task A_spare_that_could_not_be_moved_stays_and_the_download_still_finishes()
    {
        using var d = new Download { RecycleResult = Native.Recycled.Failed };
        TwoUnfinished(d);
        d.Link(File1);

        Assert.Equal(0, await d.RunAsync());
        Assert.Equal(["data(1).Ab12Cd34.bin.part", "data.bin"], d.Files);
        Assert.Contains("Could not move data(1).Ab12Cd34.bin.part to the Recycle Bin", d.Output);
    }

    [Fact]
    public async Task A_spare_given_by_hand_that_isnt_an_unfinished_download_is_never_removed()
    {
        using var d = new Download();
        // MirrorDL's own file holds more, so it is continued; the file given by hand is the spare.
        File.WriteAllBytes(d.PathOf("data.bin.part"), Holding(Size, (MB, 5 * MB / 2)));
        new PartState(Size, [(0, MB), (5 * MB / 2, Size)]).Save(d.PathOf("data.bin.part.mirrordl"));
        File.WriteAllBytes(d.PathOf("my copy.dat"), File1[..(2 * MB)]);
        d.Job.PartFiles.Add(d.PathOf("my copy.dat"));
        d.Link(File1);

        Assert.Equal(0, await d.RunAsync());
        Assert.Empty(d.Recycled);
        Assert.Equal(File1[..(2 * MB)], File.ReadAllBytes(d.PathOf("my copy.dat")));
        Assert.Contains("Kept my copy.dat: it isn't an unfinished download", d.Output);
    }

    [Fact]
    public async Task A_part_file_of_another_file_is_refused_and_left_as_it_is()
    {
        using var d = new Download();
        byte[] other = Download.Bytes(2 * MB, 9);
        File.WriteAllBytes(d.PathOf("data.Ab12Cd34.bin.part"), other);
        d.Link(File1);

        Assert.Equal(1, await d.RunAsync());
        Assert.Contains("is not the same file as the links", d.Session.ErrorMessage);
        Assert.Contains("move or delete it", d.Session.ErrorMessage);
        Assert.Equal(other, File.ReadAllBytes(d.PathOf("data.Ab12Cd34.bin.part")));
    }

    [Fact]
    public async Task An_unfinished_file_bigger_than_the_download_is_a_different_file()
    {
        using var d = new Download();
        File.WriteAllBytes(d.PathOf("data.bin.part"), new byte[Size + 1]);
        d.Link(File1);

        Assert.Equal(1, await d.RunAsync());
        Assert.Contains("is bigger than the file behind the links", d.Session.ErrorMessage);
        Assert.Equal(Size + 1, new FileInfo(d.PathOf("data.bin.part")).Length);
    }

    [Fact]
    public async Task A_damaged_progress_file_stops_with_advice()
    {
        using var d = new Download();
        File.WriteAllBytes(d.PathOf("data.bin.part"), new byte[MB]);
        File.WriteAllText(d.PathOf("data.bin.part.mirrordl"), "mirrordl 2\nsize 12\nmissing 0 x\n");
        d.Link(File1);

        Assert.Equal(1, await d.RunAsync());
        Assert.Contains("is damaged", d.Session.ErrorMessage);
    }

    [Fact]
    public async Task A_progress_file_for_another_size_is_refused()
    {
        using var d = new Download();
        File.WriteAllBytes(d.PathOf("data.bin.part"), new byte[MB]);
        new PartState(Size + 5, [(0, Size + 5)]).Save(d.PathOf("data.bin.part.mirrordl"));
        d.Link(File1);

        Assert.Equal(1, await d.RunAsync());
        Assert.Contains("was started for a file of", d.Session.ErrorMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_range_requests_an_unfinished_file_is_only_replaced_when_the_user_agrees(bool answer)
    {
        using var d = new Download { Answer = answer };
        byte[] before = File1[..MB];
        File.WriteAllBytes(d.PathOf("data.bin.part"), before);
        d.Link(File1).Ranges = false;

        Assert.Equal(answer ? 0 : 1, await d.RunAsync());
        Assert.Single(d.Questions);
        if (answer)
        {
            Assert.Equal(File1, File.ReadAllBytes(d.PathOf("data.bin")));
            Assert.Equal(["data.bin"], d.Files);
        }
        else
            Assert.Equal(before, File.ReadAllBytes(d.PathOf("data.bin.part")));
    }
}
