// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace MirrorDL.App;

/// <summary>
/// MirrorDL's browser for a download page whose link only appears after a captcha or a click: the user solves it and
/// clicks the page's download button, and MirrorDL takes the link (<see cref="LinkTaken"/>) - the browser itself
/// downloads nothing. MirrorDL never solves a captcha or clicks for the user.
/// </summary>
sealed class BrowserWindow : Window
{
    readonly WebView2 _web = new();
    readonly TextBox _address = new() { IsSpellCheckEnabled = false };
    readonly InfoBar _info = new() { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational };
    readonly Uri _start;

    /// <summary>A link the page started a download from. Raised on the window's thread.</summary>
    public event Action<Uri>? LinkTaken;

    public BrowserWindow(Uri start)
    {
        _start = start;
        Title = "MirrorDL - " + start.Host;
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "mirrordl.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 900));

        var back = IconButton("", "Back", () => { if (_web.CanGoBack) _web.GoBack(); });
        var reload = IconButton("", "Reload", () => _web.Reload());
        var outside = new Button { Content = "Open in my browser" };
        outside.Click += (_, _) => Process.Start(new ProcessStartInfo((_web.Source ?? _start).ToString()) { UseShellExecute = true });
        _address.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && Uri.TryCreate(_address.Text.Trim(), UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                _web.Source = uri;
        };
        var bar = new Grid { ColumnSpacing = 6, Padding = new Thickness(8, 6, 8, 6) };
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        Place(bar, back, 0);
        Place(bar, reload, 1);
        Place(bar, _address, 2);
        Place(bar, outside, 3);

        _info.Title = "Click the page's download button";
        _info.Message = "Solve the captcha if the page has one, then click its download button: MirrorDL takes the link and shows the " +
                        "file, ready to download. The page's own download doesn't happen.";
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(bar);
        Grid.SetRow(_info, 1);
        root.Children.Add(_info);
        Grid.SetRow(_web, 2);
        root.Children.Add(_web);
        Content = root;
        _ = StartAsync();
    }

    static Button IconButton(string glyph, string name, Action click)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    static void Place(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    async Task StartAsync()
    {
        try
        {
            // Its cookies and cache go to MirrorDL's own folder, not next to the program (which may not be writable).
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", BrowserData);
            await _web.EnsureCoreWebView2Async();
            var core = _web.CoreWebView2;
            core.DownloadStarting += Core_DownloadStarting;
            // A page that opens its download in a new tab or window: it opens here instead, so its link is taken too.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                core.Navigate(e.Uri);
            };
            core.SourceChanged += (_, _) => _address.Text = core.Source;
            _web.Source = _start;
        }
        catch (Exception e)
        {
            // No WebView2 Runtime (Windows 11 has it; a stripped-down system may not): the usual browser instead.
            _info.Severity = InfoBarSeverity.Warning;
            _info.Title = "MirrorDL's browser can't start";
            _info.Message = $"{e.Message} Use Open in my browser, and copy the download link from there.";
        }
    }

    void Core_DownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        e.Handled = true;
        if (Uri.TryCreate(e.DownloadOperation.Uri, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            _info.Severity = InfoBarSeverity.Success;
            _info.Title = "MirrorDL took the link";
            _info.Message = $"{Path.GetFileName(e.ResultFilePath)} - see the Downloads page of MirrorDL. You can close this window, or take more links.";
            LinkTaken?.Invoke(uri);
        }
        else
        {
            // A blob: or data: address: the page puts the file together in the browser itself.
            _info.Severity = InfoBarSeverity.Warning;
            _info.Title = "This button builds the file inside the browser";
            _info.Message = "MirrorDL can't take that over. Look for the page's normal download button, or a direct link.";
        }
    }

    /// <summary>Where the browser keeps its cookies and cache.</summary>
    public static string BrowserData => Path.Combine(Storage.Folder, "Browser");
}
