// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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

    static readonly Guid FileOperationId = new("3AD05575-8857-4850-9277-11B85BDB8E09");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, in Guid id, out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem { } // only handed on, never called

    // The shell's file operations. Only DeleteItem and PerformOperations are used; the others just keep their place:
    // the methods must stay in the order of shobjidl_core.h, which is all COM goes by (the same for the sink below).
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage(IntPtr message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr window);
        void ApplyPropertiesToItem(IntPtr item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IntPtr item, IntPtr newName, IntPtr sink);
        void RenameItems(IntPtr items, IntPtr newName);
        void MoveItem(IntPtr item, IntPtr folder, IntPtr newName, IntPtr sink);
        void MoveItems(IntPtr items, IntPtr folder);
        void CopyItem(IntPtr item, IntPtr folder, IntPtr newName, IntPtr sink);
        void CopyItems(IntPtr items, IntPtr folder);
        void DeleteItem(IShellItem item, IFileOperationProgressSink sink);
        void DeleteItems(IntPtr items);
        void NewItem(IntPtr folder, uint attributes, IntPtr name, IntPtr template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        void GetAnyOperationsAborted(out int aborted);
    }

    [ComImport, Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IntPtr item, IntPtr newName);
        [PreserveSig] int PostRenameItem(uint flags, IntPtr item, IntPtr newName, int result, IntPtr created);
        [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName);
        [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName, int result, IntPtr created);
        [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName);
        [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName, int result, IntPtr created);
        [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
        [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created);
        [PreserveSig] int PreNewItem(uint flags, IntPtr folder, IntPtr newName);
        [PreserveSig] int PostNewItem(uint flags, IntPtr folder, IntPtr newName, IntPtr template, uint attributes, int result, IntPtr item);
        [PreserveSig] int UpdateProgress(uint total, uint done);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    /// <summary>
    /// Stops a deletion that Windows already knows won't go to the Recycle Bin (as Electron and Qt do; it doesn't
    /// know that for a file too big for it - see <see cref="FitsInRecycleBin"/>), and is told afterwards whether the
    /// file went there or is gone for good.
    /// </summary>
    sealed class RecycleResult : IFileOperationProgressSink
    {
        const uint ToRecycleBin = 0x80;              // TSF_DELETE_RECYCLE_IF_POSSIBLE
        const int Abort = unchecked((int)0x80004004); // E_ABORT

        public bool Refused { get; private set; }
        public bool Recycled { get; private set; }
        public bool Deleted { get; private set; } // for good: by Windows, not by someone else meanwhile

        public int PreDeleteItem(uint flags, IntPtr item)
        {
            Refused = (flags & ToRecycleBin) == 0;
            return Refused ? Abort : 0;
        }

        // "created" is the item the file became in the Recycle Bin; none when it was deleted for good.
        public int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created)
        {
            Recycled = result >= 0 && created != IntPtr.Zero;
            Deleted = result >= 0 && created == IntPtr.Zero;
            return 0;
        }

        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IntPtr item, IntPtr newName) => 0;
        public int PostRenameItem(uint flags, IntPtr item, IntPtr newName, int result, IntPtr created) => 0;
        public int PreMoveItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName) => 0;
        public int PostMoveItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName, int result, IntPtr created) => 0;
        public int PreCopyItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName) => 0;
        public int PostCopyItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName, int result, IntPtr created) => 0;
        public int PreNewItem(uint flags, IntPtr folder, IntPtr newName) => 0;
        public int PostNewItem(uint flags, IntPtr folder, IntPtr newName, IntPtr template, uint attributes, int result, IntPtr item) => 0;
        public int UpdateProgress(uint total, uint done) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    /// <summary>What <see cref="Recycle"/> did with a file.</summary>
    public enum Recycled
    {
        /// <summary>It is in the Recycle Bin.</summary>
        Yes,
        /// <summary>Kept: it doesn't fit in the Recycle Bin, or its drive has none, so Windows would delete it for good.</summary>
        NoRoom,
        /// <summary>Kept: it could not be moved.</summary>
        Failed,
        /// <summary>Windows deleted it for good after all, although it looked like it fit.</summary>
        Deleted,
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RecycleBinInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string root, ref RecycleBinInfo info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumePathName(string path, char[] mountPoint, int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, char[] volume, int length);

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
    /// Moves a file to the Recycle Bin, without asking - and only there. Windows deletes a file that doesn't fit in
    /// the Recycle Bin for good instead, without a word, so such a file is kept (see <see cref="FitsInRecycleBin"/>).
    /// </summary>
    public static Recycled Recycle(string path)
    {
        if (!OperatingSystem.IsWindows()) return Recycled.NoRoom; // no Recycle Bin to put it in
        try
        {
            path = Path.GetFullPath(path);
            if (!FitsInRecycleBin(path)) return Recycled.NoRoom;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return Recycled.Failed; // gone meanwhile, or its drive or settings can't be read
        }
        // The shell's file operations work only in a single-threaded apartment, which a download's threads aren't.
        // Nothing may escape that thread: an exception there would end the whole program.
        var result = Recycled.Failed;
        var thread = new Thread(() =>
        {
            try { result = OperatingSystem.IsWindows() ? RecycleHere(path) : Recycled.NoRoom; }
            catch (Exception) { result = Recycled.Failed; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    /// <summary>
    /// Whether a file fits in its drive's Recycle Bin next to what is in it already. Windows doesn't say how it
    /// decides, and it doesn't make room by emptying the Recycle Bin: a 60 GB file went into a Recycle Bin of
    /// 97,395 MB, but files of 97.3 GB did not while it held 4 GB. So this counts the strict way - with what is
    /// in it, and MB as 1,000,000 bytes. False for a drive without a Recycle Bin, or one it is switched off for.
    /// </summary>
    [SupportedOSPlatform("windows")]
    static bool FitsInRecycleBin(string path)
    {
        var buffer = new char[1024];
        if (!GetVolumePathName(path, buffer, buffer.Length)) return false;
        string mountPoint = new string(buffer).TrimEnd('\0');
        var used = new RecycleBinInfo { Size = Marshal.SizeOf<RecycleBinInfo>() };
        if (SHQueryRecycleBin(mountPoint, ref used) != 0) return false; // also sets the drive's Recycle Bin up if new
        Array.Clear(buffer);
        if (!GetVolumeNameForVolumeMountPoint(mountPoint, buffer, buffer.Length)) return false;
        string volume = new string(buffer).TrimEnd('\0');                  // \\?\Volume{...}\
        int start = volume.IndexOf('{'), end = volume.IndexOf('}');
        if (start < 0 || end < start) return false;
        using var settings = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + volume[start..(end + 1)]);
        if (settings?.GetValue("MaxCapacity") is not int megabytes || settings.GetValue("NukeOnDelete") is 1) return false;
        // A policy (on managed PCs) can switch the Recycle Bin off or make it smaller than this setting says.
        const string Policies = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            using var policy = hive.OpenSubKey(Policies);
            if (policy?.GetValue("NoRecycleFiles") is 1 || policy?.GetValue("RecycleBinSize") != null) return false;
        }
        return new FileInfo(path).Length + used.Bytes <= (uint)megabytes * 1_000_000L; // a DWORD: unsigned
    }

    [SupportedOSPlatform("windows")]
    static Recycled RecycleHere(string path)
    {
        const uint Silent = 0x4, NoConfirmation = 0x10, AllowUndo = 0x40, NoErrorUi = 0x400, RecycleOnDelete = 0x80000;
        var sink = new RecycleResult();
        object? operation = null;
        IShellItem? item = null;
        try
        {
            operation = Activator.CreateInstance(Type.GetTypeFromCLSID(FileOperationId, throwOnError: true)!)!;
            var files = (IFileOperation)operation;
            files.SetOperationFlags(Silent | NoConfirmation | AllowUndo | NoErrorUi | RecycleOnDelete);
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out item);
            files.DeleteItem(item, sink);
            files.PerformOperations(); // what became of the file is in the sink
        }
        catch (Exception)
        {
            // Whatever went wrong (no shell, no access, gone meanwhile): what became of the file is told below.
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
            if (operation != null) Marshal.ReleaseComObject(operation);
        }
        return File.Exists(path) ? (sink.Refused ? Recycled.NoRoom : Recycled.Failed)
            : sink.Recycled ? Recycled.Yes
            : sink.Deleted ? Recycled.Deleted
            : Recycled.Failed; // gone, but not by this: another program removed it meanwhile
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
