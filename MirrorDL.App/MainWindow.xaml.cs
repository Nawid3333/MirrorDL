// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace MirrorDL.App;

/// <summary>
/// The window: Downloads (a new download, what its links lead to, the download list and the selected download),
/// History and Settings. The parts are in MainWindow.*.cs.
/// </summary>
public sealed partial class MainWindow : Window
{
    readonly Settings _settings = Settings.Load();
    readonly DispatcherQueueTimer _timer;
    readonly IntPtr _hwnd;
    Uri? _updatePage;
    bool _closing, _shutDown;
    // Questions of the downloads, one at a time: WinUI shows only one dialog at once.
    readonly SemaphoreSlim _questions = new(1);
    ContentDialog? _question;
    Run? _questionRun;

    public MainWindow(string[] args)
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "mirrordl.ico"));
        TitleBarIcon.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "logo-64.png")));
        PlaceWindow();
        VersionText.Text = AppInfo.Version;
        SaveToBox.PlaceholderText = Native.DownloadsFolder();
        SaveToBox.Text = _settings.SaveTo ?? "";
        foreach (var kind in new[] { EntryKind.Link, EntryKind.Checksum, EntryKind.Unfinished }) KeepEmptyBox(kind);
        CheckText.Text = _settings.Review ? "Check links" : "Start";

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        AppWindow.Closing += AppWindow_Closing;
        Closed += (_, _) => ShutDown();

        ApplyTheme();
        BuildSettings();
        LoadHistory();
        RestoreList();
        ApplyClickNLoad();
        ApplyClipboard();
        if (!Native.IsPackaged() && _settings.CheckForUpdates) _ = CheckForUpdateAsync();

        if (args.Contains("--start"))
            Root.Loaded += (_, _) => Take(args);
        else
        {
            Take(args);
            if (_settings.ResumeAtStart) Root.Loaded += (_, _) => ResumeAll_Click(this, new RoutedEventArgs());
        }
    }

    /// <summary>
    /// Files dropped on MirrorDL.exe, or links passed on the command line - at start, or by a later start that handed
    /// them over (<see cref="SingleInstance"/>); --start checks them and starts right away. Brings the window to the front.
    /// </summary>
    public void Take(IReadOnlyList<string> args)
    {
        if (_shutDown) return;
        foreach (string arg in args.Where(a => a != "--start"))
        {
            // A .txt file that can't be read (in use, only in the cloud) must not keep the window from opening.
            try { Distribute(arg); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowNotice(InfoBarSeverity.Warning, "Couldn't use what was given", $"{arg}: {ex.Message}");
            }
        }
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
        if (args.Contains("--start")) _ = CheckAsync(start: true);
    }

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string page = args.IsSettingsSelected ? "settings" : args.SelectedItemContainer?.Tag as string ?? "downloads";
        DownloadsPage.Visibility = page == "downloads" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "history") FilterHistory();
    }

    void ShowDownloads() => Nav.SelectedItem = DownloadsItem;

    /// <summary>A message at the top of the Downloads page.</summary>
    void ShowNotice(InfoBarSeverity severity, string title, string? message)
    {
        Notice.Severity = severity;
        Notice.Title = title;
        Notice.Message = message ?? "";
        Notice.IsOpen = true;
    }

    async Task CheckForUpdateAsync()
    {
        if (await Updates.FindNewerAsync() is not { } update) return;
        _updatePage = update.Page;
        UpdateBar.Message = $"MirrorDL {update.Version} is on GitHub; you have {AppInfo.Version}.";
        UpdateBar.IsOpen = true;
    }

    void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updatePage != null) Open(_updatePage.ToString());
    }

    /// <summary>Opens a file or web page with the program Windows has for it; says so if there is none.</summary>
    void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            ShowNotice(InfoBarSeverity.Warning, "Couldn't open it", $"{target}: {e.Message}");
        }
    }

    /// <summary>Closing while downloads run pauses them first, so their progress is saved; the list is kept for next time.</summary>
    void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        _settings.SaveTo = SaveToBox.Text.Trim();
        _settings.Save();
        if (_runs.Any(r => r.IsRunning))
        {
            args.Cancel = true;
            _closing = true;
            PauseAll_Click(this, new RoutedEventArgs());
        }
    }

    /// <summary>
    /// Once the window is closed - by the user, or by MirrorDL after pausing: nothing of it may run on. Window.Close()
    /// doesn't raise AppWindow.Closing, so this is on Closed.
    /// </summary>
    void ShutDown()
    {
        if (_shutDown) return;
        _shutDown = true;
        _closing = true;
        SaveList();
        _timer.Stop();
        _cnl?.Dispose();
        _cnl = null;
        Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged -= Clipboard_ContentChanged;
        WindowsShell.KeepAwake(false);
        foreach (var browser in _browsers.ToList()) browser.Close();
    }

    /// <summary>Asks a download's question; "no" when it is closed without an answer (Pause, closing the window).</summary>
    Task<bool> ConfirmAsync(Run run, BatchFile file, string question)
    {
        var answer = new TaskCompletionSource<bool>();
        bool queued = DispatcherQueue.TryEnqueue(async () =>
        {
            // async void: an exception here would end the app - and leave the download waiting for ever.
            bool waited = false;
            try
            {
                await _questions.WaitAsync(run.Cts?.Token ?? new CancellationToken(canceled: true));
                waited = true;
                if (run.Cts?.IsCancellationRequested != false || _closing)
                {
                    answer.TrySetResult(false); // asked just after Pause: no dialog to stand in the way of stopping
                    return;
                }
                _questionRun = run;
                _question = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot,
                    Title = file.Name,
                    Content = new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = "Yes",
                    CloseButtonText = "No",
                    DefaultButton = ContentDialogButton.Close,
                };
                answer.TrySetResult(await _question.ShowAsync() == ContentDialogResult.Primary);
            }
            catch (Exception ex)
            {
                run.Log.Add(new LogLine("Could not ask: " + ex.Message, Brushes.Caution));
                answer.TrySetResult(false);
            }
            finally
            {
                _question = null;
                _questionRun = null;
                if (waited) _questions.Release();
            }
        });
        if (!queued) answer.TrySetResult(false); // the window is going away
        return answer.Task;
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    static void ShowInExplorer(string? path)
    {
        if (path == null) return;
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\"");
    }

    /// <summary>A comfortable size that still fits on the screen, centered.</summary>
    void PlaceWindow()
    {
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = (int)Math.Min(1180 * scale, area.Width * 0.95);
        int height = (int)Math.Min(1000 * scale, area.Height * 0.95);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);
}
