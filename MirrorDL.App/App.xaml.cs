// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using Microsoft.UI.Xaml;

namespace MirrorDL.App;

public partial class App : Application
{
    Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // MirrorDL runs already: it takes what this start was given, and comes to the front.
        string[] given = [.. Environment.GetCommandLineArgs().Skip(1)];
        if (SingleInstance.HandOver(given))
        {
            Exit();
            return;
        }
        var window = new MainWindow(given);
        _window = window;
        SingleInstance.Listen(more => window.DispatcherQueue.TryEnqueue(() => window.Take(more)));
        window.Activate();
    }
}
