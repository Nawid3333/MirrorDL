// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MirrorDL;

/// <summary>Helpers for sorted lists of byte ranges [Start, End).</summary>
public static class Ranges
{
    public static List<(long Start, long End)> Normalize(IEnumerable<(long Start, long End)> ranges)
    {
        var result = new List<(long Start, long End)>();
        foreach (var (start, end) in ranges.Where(r => r.End > r.Start).OrderBy(r => r.Start))
        {
            if (result.Count > 0 && start <= result[^1].End)
                result[^1] = (result[^1].Start, Math.Max(result[^1].End, end));
            else
                result.Add((start, end));
        }
        return result;
    }

    public static long Sum(IEnumerable<(long Start, long End)> ranges) => ranges.Sum(r => r.End - r.Start);

    /// <summary>The parts of [0, size) that the normalized ranges don't cover.</summary>
    public static List<(long Start, long End)> Invert(IEnumerable<(long Start, long End)> ranges, long size)
    {
        var result = new List<(long Start, long End)>();
        long pos = 0;
        foreach (var (start, end) in ranges)
        {
            if (start > pos) result.Add((pos, start));
            pos = end;
        }
        if (pos < size) result.Add((pos, size));
        return result;
    }
}

/// <summary>
/// mirrordl's progress file next to an unfinished download ("name.part.mirrordl"): file size and missing ranges.
/// Version 2 ends with an "end" line, so that a file cut short (a power cut while the disk was still
/// writing it) is noticed instead of being read as fewer missing ranges - which would leave holes in the
/// finished file. Version 1, written by MirrorDL 1.1.0, has no such line and is still read.
/// </summary>
sealed class PartState(long size, List<(long Start, long End)> missing)
{
    public long Size { get; } = size;
    public List<(long Start, long End)> Missing { get; } = missing;

    public static PartState? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            string[] lines = File.ReadAllLines(path);
            int version = lines is [var first, ..] && first.Split(' ') is ["mirrordl", var v] ? int.Parse(v, CultureInfo.InvariantCulture) : 0;
            long size = -1;
            bool ended = false;
            var missing = new List<(long Start, long End)>();
            foreach (string line in lines.Skip(1))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts is ["size", var s]) size = long.Parse(s, CultureInfo.InvariantCulture);
                else if (parts is ["missing", var a, var b])
                    missing.Add((long.Parse(a, CultureInfo.InvariantCulture), long.Parse(b, CultureInfo.InvariantCulture)));
                else if (parts is ["end"]) ended = true;
            }
            bool complete = version == 1 || version == 2 && ended;
            if (complete && size >= 0 && missing.All(r => 0 <= r.Start && r.Start < r.End && r.End <= size))
                return new PartState(size, Ranges.Normalize(missing));
        }
        catch (Exception e) when (e is FormatException or OverflowException) { }
        throw new UserError($"{path} is damaged. Delete it and the unfinished file next to it to start over.");
    }

    /// <summary>
    /// Writes a new file, makes sure it is on the disk, and only then swaps it in: whatever happens, the
    /// progress file is the old one or the complete new one.
    /// </summary>
    public void Save(string path)
    {
        var text = new StringBuilder($"mirrordl 2\nsize {Size}\n");
        foreach (var (start, end) in Missing) text.Append(CultureInfo.InvariantCulture, $"missing {start} {end}\n");
        text.Append("end\n");
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.UTF8.GetBytes(text.ToString()));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Reads the control file aria2 keeps next to an unfinished download ("name.aria2").</summary>
/// <remarks>Format: https://aria2.github.io/manual/en/html/technical-notes.html</remarks>
static class Aria2Control
{
    public static PartState? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            byte[] data = File.ReadAllBytes(path);
            // Version 1 stores numbers big-endian, version 0 in the machine's order (little-endian on PCs).
            bool big = data[0] == 0 && data[1] == 1;
            if (!big && (data[0] | data[1]) != 0) throw new FormatException("unknown version");
            int pos = 6;                                     // version (2 bytes), extension flags (4)
            int infoHashLength = (int)U32(data, ref pos, big);
            pos += infoHashLength;                           // only used by BitTorrent
            long pieceLength = U32(data, ref pos, big);
            long total = (long)U64(data, ref pos, big);
            pos += 8;                                        // bytes uploaded
            int bitfieldLength = (int)U32(data, ref pos, big);
            long pieces = pieceLength == 0 ? -1 : (total + pieceLength - 1) / pieceLength;
            if (pieces < 0 || total < 0 || bitfieldLength != (pieces + 7) / 8 || pos + bitfieldLength > data.Length)
                throw new FormatException("bad sizes");

            // Pieces aria2 had only partly downloaded count as missing: that's at most a few MB again.
            var missing = new List<(long Start, long End)>();
            for (long i = 0; i < pieces; i++)
                if ((data[pos + i / 8] & (0x80 >> (int)(i % 8))) == 0)
                    missing.Add((i * pieceLength, Math.Min(total, (i + 1) * pieceLength)));
            return new PartState(total, Ranges.Normalize(missing));
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new UserError($"Can't read aria2's progress file {Path.GetFileName(path)}. " +
                                "Continue this download with aria2, or delete the unfinished file and its .aria2 file to start over.");
        }
    }

    static uint U32(byte[] data, ref int pos, bool big)
    {
        var span = data.AsSpan(pos, 4);
        pos += 4;
        return big ? BinaryPrimitives.ReadUInt32BigEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span);
    }

    static ulong U64(byte[] data, ref int pos, bool big)
    {
        var span = data.AsSpan(pos, 8);
        pos += 8;
        return big ? BinaryPrimitives.ReadUInt64BigEndian(span) : BinaryPrimitives.ReadUInt64LittleEndian(span);
    }
}

/// <summary>Finds unfinished downloads of a file and works out their final names.</summary>
static class PartFinder
{
    // Firefox puts 8 random characters into the name of its unfinished file and leaves an empty file
    // with the real name next to it: "setup.Ab3-xY_z.exe.part" next to "setup.exe".
    static readonly Regex FirefoxToken = new("^[A-Za-z0-9_-]{8}$");

    /// <summary>The names an unfinished file can stand for: without ".part" and, for Firefox, without its random piece.</summary>
    public static IEnumerable<string> FinalNames(string fileName)
    {
        if (!fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
        {
            yield return fileName; // aria2 can also write straight into the final name
            yield break;
        }
        string name = fileName[..^".part".Length];
        string[] pieces = name.Split('.');
        for (int i = 1; i < pieces.Length - 1; i++)
            if (FirefoxToken.IsMatch(pieces[i]))
                yield return string.Join('.', pieces.Where((_, j) => j != i));
        yield return name;
    }

    /// <summary>
    /// Whether an unfinished file comes from Firefox: its name has Firefox's random piece, or Firefox's empty
    /// placeholder lies next to it. Firefox writes a file from the start to the end, so after a crash only its
    /// end can be missing.
    /// </summary>
    public static bool IsFirefoxPart(string partPath)
    {
        if (!partPath.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return false;
        string dir = Path.GetDirectoryName(partPath)!;
        var names = FinalNames(Path.GetFileName(partPath)).ToList();
        return names.Count > 1 || names.Any(n => new FileInfo(Path.Combine(dir, n)) is { Exists: true, Length: 0 });
    }

    /// <summary>The final name for an unfinished file; a link's file name or Firefox's empty placeholder confirms which one.</summary>
    public static string FinalName(string partPath, IReadOnlyCollection<string> serverNames)
    {
        string dir = Path.GetDirectoryName(partPath)!;
        var names = FinalNames(Path.GetFileName(partPath)).ToList();
        return names.FirstOrDefault(n => serverNames.Contains(n, StringComparer.OrdinalIgnoreCase))
            ?? names.FirstOrDefault(n => new FileInfo(Path.Combine(dir, n)) is { Exists: true, Length: 0 })
            ?? names[^1];
    }

    /// <summary>Unfinished downloads of any of the names in a folder, the most promising first.</summary>
    public static List<string> Find(string dir, IReadOnlyCollection<string> names)
    {
        if (!Directory.Exists(dir)) return [];
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = Directory.EnumerateFiles(dir, "*.part")
            .Where(p => p.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && FinalNames(Path.GetFileName(p)).Any(wanted.Contains))
            .ToList();
        found.AddRange(wanted.Select(n => Path.Combine(dir, n)).Where(p => File.Exists(p) && File.Exists(p + ".aria2")));
        return found
            .OrderByDescending(p => File.Exists(p + ".mirrordl") || File.Exists(p + ".aria2"))
            .ThenByDescending(p => new FileInfo(p).Length)
            .ToList();
    }
}
