// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

// SHA-256 hex over sorted `lower(name)|version` lines from the yaml
// `packages:` block. Used as a staleness check for the winmds lockfile.
internal static class YamlPackagesHasher
{
    public static string Compute(IEnumerable<PackagePin> packages)
    {
        var pairs = packages
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => KeyValuePair.Create(p.Name, p.Version ?? string.Empty));
        return ComputeFromVersions(pairs);
    }

    // Accepts raw (name, version) pairs — used during fresh init.
    public static string ComputeFromVersions(IEnumerable<KeyValuePair<string, string>> versions)
    {
        var lines = versions
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
            .Select(kvp => $"{kvp.Key.ToLowerInvariant()}|{kvp.Value}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        var joined = string.Join("\n", lines);
        var bytes = Encoding.UTF8.GetBytes(joined);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
