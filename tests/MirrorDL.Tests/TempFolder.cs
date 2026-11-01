// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

namespace MirrorDL.Tests;

/// <summary>An empty folder of its own for one test, deleted afterwards.</summary>
sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateDirectory(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mirrordl-test-" + Guid.NewGuid().ToString("N"))).FullName;

    public string File(string name, int bytes = 0)
    {
        string path = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public void Dispose()
    {
        // Some tests make a file read-only, to prove that nothing writes to it.
        foreach (string file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            System.IO.File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Path, recursive: true);
    }
}
