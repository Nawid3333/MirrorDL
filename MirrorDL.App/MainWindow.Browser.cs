// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MirrorDL.App;

/// <summary>Download pages that need the user: opened in MirrorDL's browser (or the usual one), the link taken from there.</summary>
public sealed partial class MainWindow
{
    readonly List<BrowserWindow> _browsers = [];

    void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ReviewRow row) OpenPage(row.File);
    }

    void OpenPage(BatchFile file)
    {
        var page = file.Links.FirstOrDefault()?.Url;
        if (page == null) return;
        if (!_settings.Browser)
        {
            Process.Start(new ProcessStartInfo(page.ToString()) { UseShellExecute = true });
            ShowNotice(InfoBarSeverity.Informational, "Opened in your browser",
                "Click the page's download button there, copy the link of the download (in Firefox: right-click it in the downloads list, " +
                "Copy Download Link) and paste it into a box above.");
            return;
        }
        var browser = new BrowserWindow(page);
        browser.LinkTaken += link => TakeLinks([link.ToString()], page.Host, check: true);
        browser.Closed += (_, _) => _browsers.Remove(browser);
        _browsers.Add(browser);
        browser.Activate();
    }

    void ClearBrowserData()
    {
        if (_browsers.Count > 0)
        {
            ShowNotice(InfoBarSeverity.Warning, "Close MirrorDL's browser first", "Its data can't be cleared while it is open.");
            ShowDownloads();
            return;
        }
        try
        {
            if (Directory.Exists(BrowserWindow.BrowserData)) Directory.Delete(BrowserWindow.BrowserData, recursive: true);
            ShowNotice(InfoBarSeverity.Success, "The browser's data is cleared", "Its cookies and cache are gone; sites opened in it have forgotten you.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowNotice(InfoBarSeverity.Warning, "Not all of the browser's data could be cleared",
                "It is still in use for a few seconds after the browser was closed: try again in a moment.");
        }
        ShowDownloads();
    }
}
