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
}
