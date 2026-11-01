// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace MirrorDL.App;

/// <summary>Settings: every feature that isn't the download itself can be switched off, each with what it does.</summary>
public sealed partial class MainWindow
{
    ClickNLoad? _cnl;
    TextBlock? _cnlState;
    bool _ownCopy;
    static readonly Regex LinkPattern = new(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase);

    void BuildSettings()
    {
        Section("Downloads");
        Toggle("Sort links into files",
            "Links you paste together are sorted by the file they lead to: links to the same file - the same name, or the same size " +
            "and content - are its mirrors and are used together; links to other files become downloads of their own, one after another. " +
            "Off: all links of one check are mirrors of one file, and links with another size or content are skipped.",
            () => _settings.SortLinks, v => _settings.SortLinks = v);
        Toggle("Look for download links on web pages",
            "A link that leads to a web page - a file host's page with a download button, say - is read for links to files on it, " +
            "and those are shown instead. A link that only appears after a captcha or a click can't be seen this way: such a page " +
            "gets an Open page button.",
            () => _settings.LookIntoPages, v => _settings.LookIntoPages = v);
        Toggle("Show the files before downloading",
            "After Check links, MirrorDL lists the files the links lead to - name, size, links, notes - and you tick which ones to " +
            "download. Off: what can be downloaded starts right away. Links that come from Click'n'Load, the browser or the " +
            "clipboard are always shown first.",
            () => _settings.Review, v =>
            {
                _settings.Review = v;
                CheckText.Text = v ? "Check links" : "Start";
            });
        Toggle("Keep the PC awake while downloading",
            "Windows doesn't go to sleep while a download runs, so a long one isn't cut off at night. The screen still turns off. " +
            "Once nothing downloads, Windows sleeps as usual.",
            () => _settings.KeepAwake, v => _settings.KeepAwake = v);
        Toggle("Delete leftovers that can't go to the Recycle Bin, without asking",
            "When unfinished files of the same download are put together, the ones not needed anymore go to the Recycle Bin. Ones " +
            "that can't go there (too big for it, or their drive has none) can only be deleted for good: on, MirrorDL does that " +
            "without asking; off, it asks once the download is done.",
            () => _settings.DeleteLeftovers, v => _settings.DeleteLeftovers = v);
        Number("Connections in total, at most",
            "Empty: no limit. MirrorDL finds the best number by itself - it adds connections as long as they make the download " +
            "faster - so a limit is only needed to leave room for other programs.",
            () => _settings.Connections, v => _settings.Connections = v);
        Number("Connections per link, at most",
            "Empty: 16, as aria2. Servers that see many more connections from one address often answer with errors or block it.",
            () => _settings.PerLink, v => _settings.PerLink = v);

        Section("Links from other programs");
        _cnlState = Toggle("Click'n'Load",
            "Link sites such as filecrypt have a Click'n'Load button: once you solved their captcha in your browser, it sends the " +
            "links to a download program on this PC. MirrorDL takes them as JDownloader does (127.0.0.1, port 9666, which only this " +
            "PC can reach) and shows them for checking - nothing is downloaded by itself. Only one program can take them: close " +
            "JDownloader to use it here.",
            () => _settings.ClickNLoad, v => _settings.ClickNLoad = v, ApplyClickNLoad, withState: true);
        Toggle("Open download pages in MirrorDL's browser",
            "A web page whose download button needs a captcha or a click opens in a window of MirrorDL: solve the captcha, click the " +
            "button, and MirrorDL takes the link - the page's own download doesn't happen. Its cookies are kept in MirrorDL's folder " +
            "on this PC. Off: such pages open in your usual browser, and you copy the link from there.",
            () => _settings.Browser, v => _settings.Browser = v);
        ButtonCard(" ", "Clear the browser's cookies and data", "Signs you out of the sites opened in MirrorDL's browser.", ClearBrowserData);
        Toggle("Watch the clipboard for links",
            "Links you copy anywhere are put into the new download's boxes, ready to be checked. Nothing is downloaded by itself.",
            () => _settings.WatchClipboard, v => _settings.WatchClipboard = v, ApplyClipboard);

        Section("Window");
        Toggle("Show the progress on the taskbar button",
            "The taskbar button of MirrorDL fills up as the downloads go, so you see it without opening the window.",
            () => _settings.TaskbarProgress, v => _settings.TaskbarProgress = v);
        Toggle("Flash the taskbar button when downloads are done",
            "Only when MirrorDL isn't the window in front.",
            () => _settings.FlashWhenDone, v => _settings.FlashWhenDone = v);

        Section("What MirrorDL keeps");
        Toggle("Keep unfinished downloads when MirrorDL closes",
            "The download list is kept: after closing MirrorDL or restarting the PC, paused downloads are still there - press Resume. " +
            "Off: the list starts empty next time, and to continue a download you paste its links again (the progress is kept in " +
            "the unfinished file either way).",
            () => _settings.KeepList, v => _settings.KeepList = v, SaveList);
        Toggle("Resume downloads when MirrorDL starts",
            "Paused and waiting downloads of the list go on by themselves when MirrorDL is started.",
            () => _settings.ResumeAtStart, v => _settings.ResumeAtStart = v);
        Toggle("Keep a history of finished downloads",
            "Finished downloads are listed in History, with their links, to find or download them again. Off: nothing is recorded, " +
            "and the history kept so far is deleted. The downloaded files always stay.",
            () => _settings.KeepHistory, v => _settings.KeepHistory = v, () =>
            {
                if (!_settings.KeepHistory) ClearHistory();
                FilterHistory();
            });
        if (!Native.IsPackaged())
            Toggle("Check for a new version at start",
                "Asks GitHub once at start whether a newer MirrorDL is out, and sends nothing else. (The Store version is updated " +
                "by the Store and never asks.)",
                () => _settings.CheckForUpdates, v => _settings.CheckForUpdates = v);
        ButtonCard("Where it is kept", "Open the folder",
            $"Settings, the download list, the history and the browser's data are in {Storage.Folder}, on this PC only.",
            () => Process.Start("explorer.exe", $"\"{Directory.CreateDirectory(Storage.Folder).FullName}\""));
    }

    void Section(string title) =>
        SettingsPanel.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Margin = new Thickness(0, 14, 0, 2),
        });

    /// <summary>A card: the setting's name and what it does on the left, <paramref name="control"/> on the right.</summary>
    StackPanel Card(string title, string help, FrameworkElement control)
    {
        var grid = new Grid { Style = (Style)Root.Resources["SettingCard"] };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2 };
        if (title.Trim().Length > 0)
            text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = help, Style = (Style)Root.Resources["Help"] });
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        AutomationHelp(control, title, help);
        SettingsPanel.Children.Add(grid);
        return text;
    }

    static void AutomationHelp(FrameworkElement control, string title, string help)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, title.Trim().Length > 0 ? title : help);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(control, help);
    }

    TextBlock? Toggle(string title, string help, Func<bool> get, Action<bool> set, Action? after = null, bool withState = false)
    {
        var toggle = new ToggleSwitch { IsOn = get(), OnContent = "On", OffContent = "Off", MinWidth = 0 };
        toggle.Toggled += (_, _) =>
        {
            set(toggle.IsOn);
            _settings.Save();
            after?.Invoke();
            UpdateList();
        };
        var text = Card(title, help, toggle);
        if (!withState) return null;
        var state = new TextBlock { Style = (Style)Root.Resources["Help"] };
        text.Children.Add(state);
        return state;
    }

    void Number(string title, string help, Func<int?> get, Action<int?> set)
    {
        var box = new NumberBox
        {
            Minimum = 1,
            Maximum = 64,
            PlaceholderText = "no limit",
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 150,
            Value = get() ?? double.NaN,
        };
        box.ValueChanged += (_, args) =>
        {
            set(double.IsNaN(args.NewValue) ? null : (int)Math.Clamp(args.NewValue, 1, 64));
            _settings.Save();
        };
        Card(title, help, box);
    }

    void ButtonCard(string title, string button, string help, Action action)
    {
        var control = new Button { Content = button };
        control.Click += (_, _) => action();
        Card(title, help, control);
    }

    /// <summary>Starts or stops listening for Click'n'Load as the setting says, and tells whether it listens.</summary>
    void ApplyClickNLoad()
    {
        if (_settings.ClickNLoad && _cnl == null)
        {
            _cnl = ClickNLoad.Start();
            if (_cnl != null)
                _cnl.Received += (links, source) => DispatcherQueue.TryEnqueue(() =>
                    TakeLinks(links, "Click'n'Load" + (Uri.TryCreate(source, UriKind.Absolute, out var page) ? $" ({page.Host})" : ""), check: true));
        }
        else if (!_settings.ClickNLoad && _cnl != null)
        {
            _cnl.Dispose();
            _cnl = null;
        }
        if (_cnlState != null)
            _cnlState.Text = !_settings.ClickNLoad ? ""
                : _cnl != null ? $"Listening on port {_cnl.Port}."
                : $"Port {ClickNLoad.DefaultPort} is used by another program (JDownloader?), so MirrorDL can't take links from Click'n'Load now. Close it and switch this off and on.";
    }

    void ApplyClipboard()
    {
        Clipboard.ContentChanged -= Clipboard_ContentChanged;
        if (_settings.WatchClipboard) Clipboard.ContentChanged += Clipboard_ContentChanged;
    }

    void Clipboard_ContentChanged(object? sender, object e) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (_ownCopy)
        {
            _ownCopy = false;
            return;
        }
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            string text = await content.GetTextAsync();
            var links = LinkPattern.Matches(text).Select(m => m.Value.TrimEnd('.', ',', ';', ')', ']')).Distinct().Take(500).ToList();
            if (links.Count > 0) TakeLinks(links, "the clipboard", check: false);
        }
        catch (Exception)
        {
            // Another program holds the clipboard, or put something that can't be read: nothing to take.
        }
    });
}
