// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Buffers.Binary;

namespace MirrorDL.Tests;

public class PartFinderTests
{
    [Theory]
    [InlineData("setup.Ab3-xY_z.exe.part", "setup.exe")]
    [InlineData("ubuntu-24.04.Kk7-Mm9_.iso.part", "ubuntu-24.04.iso")]
    [InlineData("file.zip.part", "file.zip")]
    public void FinalNames_include_the_name_without_part_and_without_Firefox_random_piece(string part, string expected) =>
        Assert.Contains(expected, PartFinder.FinalNames(part));

    [Fact]
    public void FinalNames_of_a_file_without_part_is_the_file_itself() =>
        Assert.Equal(["data.bin"], PartFinder.FinalNames("data.bin"));

    [Fact]
    public void FinalName_prefers_the_name_a_link_gives()
    {
        using var folder = new TempFolder();
        string part = folder.File("data.Ab12Cd34.bin.part");
        Assert.Equal("data.bin", PartFinder.FinalName(part, ["data.bin"]));
    }

    [Fact]
    public void FinalName_finds_Firefox_empty_placeholder_when_the_links_name_differs()
    {
        using var folder = new TempFolder();
        folder.File("report.pdf");
        string part = folder.File("report.Zz9_Qq8w.pdf.part", 10);
        Assert.Equal("report.pdf", PartFinder.FinalName(part, ["something-else.pdf"]));
    }

    [Fact]
    public void FinalName_without_any_clue_only_drops_part()
    {
        using var folder = new TempFolder();
        string part = folder.File("report.Zz9_Qq8w.pdf.part", 10);
        Assert.Equal("report.Zz9_Qq8w.pdf", PartFinder.FinalName(part, []));
    }

    [Fact]
    public void Find_puts_the_download_with_a_progress_file_first_and_ignores_other_files()
    {
        using var folder = new TempFolder();
        string withProgress = folder.File("a.Ab12Cd34.bin.part", 10);
        folder.File("a.Ab12Cd34.bin.part.aria2", 10);
        string bigger = folder.File("a.Zz9_Qq8w.bin.part", 1000);
        folder.File("b.bin.part", 5000);

        Assert.Equal([withProgress, bigger], PartFinder.Find(folder.Path, ["a.bin"]));
    }
}

public class PartStateTests
{
    [Fact]
    public void Save_and_Load_give_back_the_same_ranges()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "x.part.mirrordl");
        new PartState(1000, [(0, 100), (500, 1000)]).Save(path);

        var loaded = PartState.Load(path)!;
        Assert.Equal(1000, loaded.Size);
        List<(long Start, long End)> expected = [(0, 100), (500, 1000)];
        Assert.Equal(expected, loaded.Missing);
    }

    [Fact]
    public void Load_of_a_missing_file_is_null() => Assert.Null(PartState.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mirrordl")));

    [Fact]
    public void Load_of_a_damaged_file_says_so()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "x.part.mirrordl");
        File.WriteAllText(path, "mirrordl 1\nsize 100\nmissing 50 200\n"); // a range past the end
        Assert.Throws<UserError>(() => PartState.Load(path));
    }
}

public class Aria2ControlTests
{
    const uint Piece = 1 << 20;

    /// <summary>A control file as aria2 writes it: no BitTorrent info hash and no pieces in flight.</summary>
    static byte[] ControlFile(bool bigEndian, ulong total, params byte[] bitfield)
    {
        var data = new List<byte>(bigEndian ? [0, 1] : [0, 0]);
        void U32(uint v) { var b = new byte[4]; if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(b, v); else BinaryPrimitives.WriteUInt32LittleEndian(b, v); data.AddRange(b); }
        void U64(ulong v) { var b = new byte[8]; if (bigEndian) BinaryPrimitives.WriteUInt64BigEndian(b, v); else BinaryPrimitives.WriteUInt64LittleEndian(b, v); data.AddRange(b); }
        U32(0);              // extension flags
        U32(0);              // info hash length
        U32(Piece);          // piece length
        U64(total);          // total length
        U64(0);              // uploaded
        U32((uint)bitfield.Length);
        data.AddRange(bitfield);
        U32(0);              // pieces in flight
        return [.. data];
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Load_turns_the_missing_pieces_into_ranges(bool bigEndian)
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "x.part.aria2");
        // 5.5 pieces; pieces 0, 1 and 3 done (bits 1101 0000), so 2 and 4-5 are missing.
        File.WriteAllBytes(path, ControlFile(bigEndian, 5 * Piece + Piece / 2, 0b1101_0000));

        var state = Aria2Control.Load(path)!;
        Assert.Equal(5 * Piece + Piece / 2, state.Size);
        List<(long Start, long End)> expected = [(2 * Piece, 3 * Piece), (4 * Piece, 5 * Piece + Piece / 2)];
        Assert.Equal(expected, state.Missing);
    }

    [Fact]
    public void Load_of_a_damaged_file_says_so()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "x.part.aria2");
        File.WriteAllBytes(path, ControlFile(true, 20 * Piece, 0xFF)); // 20 pieces need 3 bytes of bitfield
        Assert.Throws<UserError>(() => Aria2Control.Load(path));
    }
}
