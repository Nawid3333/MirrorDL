// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace MirrorDL;

/// <summary>Checksums pasted with the links: MD5, SHA-1, SHA-256, SHA-384 or SHA-512, told apart by their length.</summary>
public static class Checksum
{
    public static HashAlgorithmName? Algorithm(string hex) => hex.Length switch
    {
        32 => HashAlgorithmName.MD5,
        40 => HashAlgorithmName.SHA1,
        64 => HashAlgorithmName.SHA256,
        96 => HashAlgorithmName.SHA384,
        128 => HashAlgorithmName.SHA512,
        _ => null,
    };

    /// <summary>Accepts "0123abcd..." as well as "sha256:0123abcd...".</summary>
    public static bool TryParse(string token, [NotNullWhen(true)] out string? hex)
    {
        hex = token[(token.LastIndexOf(':') + 1)..].ToLowerInvariant();
        return Algorithm(hex) != null && hex.All(char.IsAsciiHexDigit);
    }

    /// <summary>Reads the file once and computes every expected checksum; reports (bytes read, total) as it goes.</summary>
    public static async Task<List<(string Algorithm, string Expected, string Actual)>> ComputeAsync(
        string path, IReadOnlyList<string> expected, Action<long, long> progress, CancellationToken ct)
    {
        var checks = expected.Select(hex => (Expected: hex, Algorithm: Algorithm(hex)!.Value))
            .Select(c => (c.Expected, c.Algorithm, Hash: IncrementalHash.CreateHash(c.Algorithm))).ToList();
        var buffer = new byte[4 << 20];
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            long length = stream.Length, done = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                foreach (var c in checks) c.Hash.AppendData(buffer, 0, read);
                done += read;
                progress(done, length);
            }
        }
        var results = new List<(string, string, string)>();
        foreach (var c in checks)
        {
            results.Add((c.Algorithm.Name!, c.Expected, Convert.ToHexStringLower(c.Hash.GetHashAndReset())));
            c.Hash.Dispose();
        }
        return results;
    }
}
