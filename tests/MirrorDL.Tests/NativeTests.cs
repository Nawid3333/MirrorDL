// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

namespace MirrorDL.Tests;

public class NativeTests
{
    [Fact]
    public void HasHoles_finds_parts_of_a_sparse_file_that_were_never_written()
    {
        using var folder = new TempFolder();
        const long size = 8 << 20;

        string full = folder.File("full.bin", (int)size);              // written from start to end, not sparse
        using (var handle = File.OpenHandle(full))
            Assert.False(Native.HasHoles(handle, size));

        string gaps = Path.Combine(folder.Path, "gaps.bin");            // sparse, only its end written
        using (var handle = File.OpenHandle(gaps, FileMode.Create, FileAccess.ReadWrite))
        {
            Native.SetSparse(handle, true);
            RandomAccess.Write(handle, new byte[1 << 20], size - (1 << 20));
            Assert.True(Native.HasHoles(handle, size));
            Assert.False(Native.HasHoles(handle, 0));
        }
    }

    [Fact]
    public void Recycle_of_a_file_that_is_gone_fails_and_touches_nothing()
    {
        using var folder = new TempFolder();
        Assert.Equal(Native.Recycled.Failed, Native.Recycle(Path.Combine(folder.Path, "gone.bin")));
    }

    [Fact]
    public void DownloadsFolder_is_a_folder_that_exists() => Assert.True(Directory.Exists(Native.DownloadsFolder()));

    [Fact]
    public void Tests_run_unpackaged_and_without_a_console_of_their_own()
    {
        Assert.False(Native.IsPackaged());
        Assert.False(Native.OwnsConsole());
    }

    [Fact]
    public void Flush_writes_through_to_the_disk_without_complaint()
    {
        using var folder = new TempFolder();
        using var handle = File.OpenHandle(Path.Combine(folder.Path, "f.bin"), FileMode.Create, FileAccess.ReadWrite);
        RandomAccess.Write(handle, new byte[] { 1, 2, 3 }, 0);
        Native.Flush(handle);
        Assert.Equal(3, RandomAccess.GetLength(handle));
    }

    [Fact]
    public void Recycle_keeps_a_file_too_big_for_the_Recycle_Bin_instead_of_deleting_it_for_good()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows has a Recycle Bin.");
        using var folder = new TempFolder();
        Assert.SkipUnless(new DriveInfo(folder.Path).DriveFormat is "NTFS" or "ReFS", "The temp folder's drive has no sparse files.");
        string huge = Path.Combine(folder.Path, "huge.bin");
        // Bigger than its whole drive, so bigger than the drive's Recycle Bin too; sparse, so it takes no room.
        // Windows would delete it for good without a word (tried: 1.8 TB on a 1.7 TB drive).
        using (var handle = File.OpenHandle(huge, FileMode.Create, FileAccess.ReadWrite))
        {
            Native.SetSparse(handle, true);
            RandomAccess.SetLength(handle, new DriveInfo(folder.Path).TotalSize + (1L << 30));
        }

        Assert.Equal(Native.Recycled.NoRoom, Native.Recycle(huge));
        Assert.True(File.Exists(huge));
    }
}
