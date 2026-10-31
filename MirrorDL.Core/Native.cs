// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MirrorDL;

/// <summary>The few Windows functions .NET doesn't offer directly.</summary>
public static class Native
{
    const uint FsctlSetSparse = 0x000900C4;
    const uint FsctlQueryAllocatedRanges = 0x000940CF;
    static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref byte input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    static extern bool DeviceIoControlRanges(SafeFileHandle device, uint code, long[] input, int inputSize,
        long[] output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FlushFileBuffers(SafeFileHandle file);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileOperation
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        public int Aborted;
        public IntPtr NameMappings;
        public string? Title;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref ShFileOperation operation);

    [DllImport("kernel32.dll")]
    static extern uint GetConsoleProcessList(uint[] processes, uint count);

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);

    [DllImport("kernel32.dll")]
    static extern int GetCurrentPackageFullName(ref uint length, IntPtr name);

    /// <summary>True when running as an installed MSIX package, which is how the Store version runs.</summary>
    public static bool IsPackaged()
    {
        if (!OperatingSystem.IsWindows()) return false;
        uint length = 0;
        const int NoPackage = 15700; // APPMODEL_ERROR_NO_PACKAGE
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) != NoPackage;
    }

    /// <summary>
    /// Marks a file as sparse, so that writing far into it doesn't make Windows fill everything before with
    /// zeros first. Does nothing on drives without sparse files (FAT32, exFAT).
    /// </summary>
    public static void SetSparse(SafeFileHandle file, bool sparse)
    {
        if (!OperatingSystem.IsWindows()) return;
        byte flag = sparse ? (byte)1 : (byte)0;
        DeviceIoControl(file, FsctlSetSparse, ref flag, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    /// <summary>
    /// Whether the first <paramref name="length"/> bytes of a file may have parts that were never written: only a
    /// sparse file can, where the drive keeps no space for them. True also when the drive can't tell.
    /// </summary>
    public static bool HasHoles(SafeFileHandle file, long length)
    {
        if (!OperatingSystem.IsWindows() || length <= 0 || (File.GetAttributes(file) & FileAttributes.SparseFile) == 0)
            return false;
        long[] query = [0, length], ranges = new long[4]; // (offset, length) pairs: room for two
        if (!DeviceIoControlRanges(file, FsctlQueryAllocatedRanges, query, 16, ranges, ranges.Length * 8, out int returned, IntPtr.Zero))
            return true; // more pieces than fit (gaps between them), or no answer
        return returned != 16 || ranges[0] > 0 || ranges[0] + ranges[1] < length;
    }

    /// <summary>
    /// Moves a file to the Recycle Bin, without asking. On a drive without one, or for a file too big for it,
    /// Windows deletes it instead. True when the file is gone from its place.
    /// </summary>
    public static bool Recycle(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Delete(path);
            return true;
        }
        const uint Delete = 3;                                                    // FO_DELETE
        const ushort Silent = 0x4, NoConfirmation = 0x10, AllowUndo = 0x40, NoErrorUi = 0x400;
        var operation = new ShFileOperation
        {
            Function = Delete,
            From = Path.GetFullPath(path) + "\0", // the list ends with a second \0, which the marshaller adds
            Flags = AllowUndo | NoConfirmation | Silent | NoErrorUi,
        };
        return SHFileOperation(ref operation) == 0 && operation.Aborted == 0 && !File.Exists(path);
    }

    /// <summary>Makes sure what was written to the file is on the disk, not only in the cache.</summary>
    public static void Flush(SafeFileHandle file)
    {
        if (OperatingSystem.IsWindows()) FlushFileBuffers(file);
    }

    /// <summary>True when this program has a console window of its own (started by double-click), which closes when it ends.</summary>
    public static bool OwnsConsole()
    {
        if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected) return false;
        return GetConsoleProcessList(new uint[2], 2) == 1;
    }

    public static string DownloadsFolder()
    {
        if (OperatingSystem.IsWindows() && SHGetKnownFolderPath(DownloadsFolderId, 0, IntPtr.Zero, out IntPtr path) == 0)
        {
            try { return Marshal.PtrToStringUni(path)!; }
            finally { Marshal.FreeCoTaskMem(path); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
