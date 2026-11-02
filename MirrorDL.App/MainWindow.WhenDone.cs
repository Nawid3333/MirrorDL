// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MirrorDL.App;

/// <summary>What the PC does once the downloads are done.</summary>
enum WhenDoneAction { Sleep, Hibernate, ShutDown }

/// <summary>
/// The "When done" box of the download list: sleep, hibernate or shut down once nothing runs anymore, after a countdown
/// that can be cancelled. Only for this start: the box begins at "Do nothing" every time, so a choice made today can't
/// turn the PC off another day.
/// </summary>
public sealed partial class MainWindow
{
    const int CountdownSeconds = 60;
    ContentDialog? _countdown;

    /// <summary>Sleep and Hibernate are offered only when this PC can do them (hibernation can be switched off).</summary>
    void WhenDoneBox_Loaded(object sender, RoutedEventArgs e)
    {
        var (sleep, hibernate) = WindowsShell.PowerOptions();
        if (!sleep) WhenDoneBox.Items.Remove(WhenDoneSleep);
        if (!hibernate) WhenDoneBox.Items.Remove(WhenDoneHibernate);
    }

    WhenDoneAction? WhenDoneChosen()
    {
        var item = WhenDoneBox.SelectedItem as ComboBoxItem;
        return item == WhenDoneSleep ? WhenDoneAction.Sleep
            : item == WhenDoneHibernate ? WhenDoneAction.Hibernate
            : item == WhenDoneShutDown ? WhenDoneAction.ShutDown
            : null;
    }

    /// <summary>The last running download ended by itself: after a countdown, the PC does what the box says.</summary>
    async void StartWhenDone()
    {
        if (WhenDoneChosen() is not { } action || _countdown != null) return;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
        WindowsShell.Flash(_hwnd);

        int left = CountdownSeconds;
        bool timeUp = false;
        var dialog = _countdown = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = CountdownTitle(action, left),
            Content = new TextBlock { Text = "All downloads are done. Cancel to keep the PC on.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Now",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) =>
        {
            if (--left > 0) dialog.Title = CountdownTitle(action, left);
            else
            {
                timeUp = true;
                dialog.Hide();
            }
        };
        var result = ContentDialogResult.None;
        try
        {
            timer.Start();
            result = await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // WinUI shows one dialog at a time. Going off without the countdown would be worse than staying on.
            ShowNotice(InfoBarSeverity.Warning, "The PC stays on", $"The countdown couldn't be shown: {ex.Message}");
        }
        finally
        {
            timer.Stop();
            _countdown = null;
        }
        if (_shutDown || _closing) return;
        // Esc closes the dialog the way the end of the countdown does: only that end and "Now" go on.
        if (result != ContentDialogResult.Primary && !timeUp)
        {
            WhenDoneBox.SelectedIndex = 0;
            return;
        }
        // A download that started meanwhile ends first; the countdown comes again then.
        if (_runs.Any(r => r.IsRunning)) return;
        WhenDoneBox.SelectedIndex = 0; // once: waking up from sleep doesn't do it again
        DoWhenDone(action);
    }

    /// <summary>Sleeps, hibernates or shuts down; says so if Windows refused.</summary>
    void DoWhenDone(WhenDoneAction action)
    {
        // The way to try this without the PC going off: MIRRORDL_POWER_TEST=1 shows a notice instead.
        if (Environment.GetEnvironmentVariable("MIRRORDL_POWER_TEST") == "1")
        {
            ShowNotice(InfoBarSeverity.Informational, $"Test: would {Verb(action)} now", "MIRRORDL_POWER_TEST is set.");
            return;
        }
        string? error = action switch
        {
            WhenDoneAction.Sleep => WindowsShell.Sleep(),
            WhenDoneAction.Hibernate => WindowsShell.Hibernate(),
            _ => WindowsShell.ShutDown(),
        };
        if (error != null) ShowNotice(InfoBarSeverity.Error, $"Couldn't {Verb(action)}", error);
    }

    static string Verb(WhenDoneAction action) => action switch
    {
        WhenDoneAction.Sleep => "go to sleep",
        WhenDoneAction.Hibernate => "hibernate",
        _ => "shut down",
    };

    static string CountdownTitle(WhenDoneAction action, int seconds) => action switch
    {
        WhenDoneAction.Sleep => $"Going to sleep in {seconds} seconds",
        WhenDoneAction.Hibernate => $"Hibernating in {seconds} seconds",
        _ => $"Shutting down in {seconds} seconds",
    };
}
