// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MirrorDL;

/// <summary>The few Windows functions .NET doesn't offer directly.</summary>
public static class Native
{
    const uint FsctlSetSparse = 0x000900C4;
    static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref byte input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FlushFileBuffers(SafeFileHandle file);

    [DllImport("kernel32.dll")]
    static extern uint GetConsoleProcessList(uint[] processes, uint count);

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);

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
