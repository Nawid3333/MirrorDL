// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MirrorDL.App;

/// <summary>The theme: as Windows, light, dark, or OLED black - a pure black window, whose pixels an OLED screen turns off.</summary>
public sealed partial class MainWindow
{
    public static readonly (string Key, string Name)[] Themes =
        [("System", "As Windows"), ("Light", "Light"), ("Dark", "Dark"), ("Black", "OLED black")];

    // What OLED black puts in place of the dark theme's colors: black, with cards just above it so that they still
    // stand out, and their edges a little brighter.
    static readonly (string Key, Color Color)[] BlackColors =
    [
        ("CardBackgroundFillColorDefaultBrush", Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C)),
        ("CardStrokeColorDefaultBrush", Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A)),
        ("NavigationViewTopPaneBackground", Colors.Black),
        ("NavigationViewContentBackground", Colors.Black),
        ("NavigationViewContentGridBorderBrush", Colors.Transparent),
        ("LayerFillColorDefaultBrush", Colors.Black),
        ("ExpanderHeaderBackground", Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C)),
        ("ExpanderContentBackground", Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C)),
    ];

    void ApplyTheme()
    {
        bool black = _settings.Theme == "Black";
        var theme = _settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" or "Black" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        // Mica shows the desktop's colors through the window; OLED black is black instead.
        SystemBackdrop = black ? null : new MicaBackdrop();
        Root.Background = black ? new SolidColorBrush(Colors.Black) : null;
        foreach (var (key, color) in BlackColors)
        {
            if (black) Root.Resources[key] = new SolidColorBrush(color);
            else Root.Resources.Remove(key);
        }
        // Theme colors are looked up again only when the theme changes: switched away and back.
        Root.RequestedTheme = theme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        Root.RequestedTheme = theme;
    }
}
