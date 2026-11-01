// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Runtime.InteropServices;

namespace MirrorDL.App;

/// <summary>The taskbar button's progress and flashing, and keeping the PC awake - Windows' own ways.</summary>
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
}
