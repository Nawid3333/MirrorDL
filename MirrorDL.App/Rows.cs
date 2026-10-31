// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MirrorDL.App;

/// <summary>The theme brushes for good, warning, error and secondary text.</summary>
static class Brushes
{
    public static Brush Normal => Get("TextFillColorPrimaryBrush");
    public static Brush Secondary => Get("TextFillColorSecondaryBrush");
    public static Brush Good => Get("SystemFillColorSuccessBrush");
    public static Brush Caution => Get("SystemFillColorCautionBrush");
    public static Brush Critical => Get("SystemFillColorCriticalBrush");
    public static Brush Accent => Get("AccentFillColorDefaultBrush");

    public static Brush For(LogKind kind) => kind switch
    {
        LogKind.Ok => Good,
        LogKind.Warn => Caution,
        LogKind.Error => Critical,
        LogKind.Dim => Secondary,
        _ => Normal,
    };

    static Brush Get(string key) => (Brush)Application.Current.Resources[key];
}

public sealed class LogLine(string text, Brush brush)
{
    public string Text { get; } = text;
    public Brush Brush { get; } = brush;
}

/// <summary>One row of the links table.</summary>
public sealed class LinkRow(Mirror link) : INotifyPropertyChanged
{
    readonly Mirror _link = link;
    string _server = link.Server, _size = "", _connections = "", _speed = "", _downloaded = "", _status = "waiting";
    Brush _statusBrush = Brushes.Secondary;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Number { get; } = $"#{link.Index}";
    public string Url { get; } = link.Url.ToString();
    public string Server { get => _server; private set => Set(ref _server, value); }
    public string Size { get => _size; private set => Set(ref _size, value); }
    public string Connections { get => _connections; private set => Set(ref _connections, value); }
    public string Speed { get => _speed; private set => Set(ref _speed, value); }
    public string Downloaded { get => _downloaded; private set => Set(ref _downloaded, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }

    public void Update(LinkStatus s, Phase phase)
    {
        Server = _link.Server;
        Size = _link.Size is long size ? Format.Bytes(size) : "";
        Connections = s.Connections > 0 ? s.Connections.ToString() : "";
        Speed = s.Connections > 0 ? Format.Speed(s.Speed) : "";
        Downloaded = s.Bytes > 0 ? Format.Bytes(s.Bytes) : "";
        (Status, StatusBrush) =
            _link.Problem != null ? (_link.Problem, Brushes.Caution)
            : s.Retrying ? ("retrying: " + _link.LastError, Brushes.Caution)
            : phase is Phase.Starting or Phase.Checking ? ("checking…", Brushes.Secondary)
            : phase == Phase.Comparing && _link.Samples == null ? ("comparing content…", Brushes.Secondary)
            : s.Connections > 0 ? ("downloading", Brushes.Normal)
            : _link.Note != null ? ("OK, but " + _link.Note, Brushes.Caution)
            : ("OK", Brushes.Good);
    }

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
