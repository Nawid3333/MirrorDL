// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MirrorDL.App;

/// <summary>The taskbar button's progress and flashing, and the PC's power: keeping it awake, sleep, hibernate, shut down.</summary>
static class WindowsShell
{
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int state);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    class TaskbarList;

    public enum Progress { None = 0, Indeterminate = 1, Normal = 2, Error = 4, Paused = 8 }

    static ITaskbarList3? _taskbar;

    /// <summary>Shows the progress on the window's taskbar button; <paramref name="percent"/> null for none.</summary>
    public static void SetProgress(IntPtr hwnd, Progress state, double percent = 0)
    {
        try
        {
            if (_taskbar == null)
            {
                _taskbar = (ITaskbarList3)new TaskbarList();
                _taskbar.HrInit();
            }
            _taskbar.SetProgressState(hwnd, (int)state);
            if (state is Progress.Normal or Progress.Paused or Progress.Error)
                _taskbar.SetProgressValue(hwnd, (ulong)Math.Clamp(percent * 10, 0, 1000), 1000);
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            // No taskbar (Explorer restarting, say): nothing to show it on.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FlashInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FlashWindowEx(ref FlashInfo info);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    /// <summary>Flashes the taskbar button until the window is looked at, unless it is in front already.</summary>
    public static void Flash(IntPtr hwnd)
    {
        if (GetForegroundWindow() == hwnd) return;
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Hwnd = hwnd,
            Flags = 0x2 | 0xC, // the taskbar button, until the window comes to the front
        };
        FlashWindowEx(ref info);
    }

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);

    /// <summary>
    /// Keeps Windows from going to sleep while downloads run (the screen may still turn off), or lets it again.
    /// Call it from the window's thread, which lives as long as the window.
    /// </summary>
    public static void KeepAwake(bool awake) =>
        SetThreadExecutionState(awake ? 0x80000000u | 0x00000001u : 0x80000000u); // ES_CONTINUOUS | ES_SYSTEM_REQUIRED

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool IsPwrSuspendAllowed();

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool IsPwrHibernateAllowed();

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvents);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool LookupPrivilegeValueW(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState, uint length, IntPtr previous, IntPtr returned);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint InitiateShutdownW(string? machine, string? message, uint graceSeconds, uint flags, uint reason);

    [StructLayout(LayoutKind.Sequential)]
    struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    /// <summary>Whether this PC can sleep and hibernate, as Windows says: hibernation can be switched off.</summary>
    public static (bool Sleep, bool Hibernate) PowerOptions() => (IsPwrSuspendAllowed(), IsPwrHibernateAllowed());

    /// <summary>Puts the PC to sleep; null when it worked, else what went wrong.</summary>
    public static string? Sleep() => Suspend(hibernate: false);

    /// <summary>Hibernates the PC; null when it worked, else what went wrong.</summary>
    public static string? Hibernate() => Suspend(hibernate: true);

    static string? Suspend(bool hibernate) =>
        SetSuspendState(hibernate, false, false) ? null : new Win32Exception().Message;

    /// <summary>Shuts the PC down: programs with unsaved work may refuse and keep it on. null when it worked.</summary>
    public static string? ShutDown()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0028, out IntPtr token)) // TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES
            return new Win32Exception().Message;
        try
        {
            if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out var luid))
                return new Win32Exception().Message;
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x0002 }; // SE_PRIVILEGE_ENABLED
            // It can succeed without giving the privilege (ERROR_NOT_ALL_ASSIGNED): only the last error tells.
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastPInvokeError() != 0)
                return new Win32Exception().Message;
            uint error = InitiateShutdownW(null, null, 0, 0x8, 0x00040000 | 0x80000000u); // power off, planned, by an application
            return error == 0 ? null : new Win32Exception((int)error).Message;
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
