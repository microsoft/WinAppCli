// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

// Thin file-I/O wrapper around WinappConfigDocument. The YAML grammar
// (parsing, splicing, rendering) lives in WinappConfigDocument so this
// service stays small and grammar evolutions don't leak into the
// service-surface tests.
internal sealed class ConfigService : IConfigService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public FileInfo ConfigPath { get; set; }

    public ConfigService(ICurrentDirectoryProvider currentDirectoryProvider)
    {
        var workingDir = currentDirectoryProvider.GetCurrentDirectory();
        ConfigPath = new FileInfo(Path.Combine(workingDir, "winapp.yaml"));
    }

    public bool Exists()
    {
        // Guard BEFORE probing the filesystem: ConfigPath.Exists internally
        // hits FindFirstFile which on a symlinked / UNC ancestor would
        // negotiate SMB (NTLM leak) before we could refuse. Run the
        // string-only containment + reparse check first.
        GuardConfigPath();
        ConfigPath.Refresh();
        return ConfigPath.Exists;
    }

    public WinappConfig Load()
    {
        if (!Exists())
        {
            return new WinappConfig();
        }

        GuardConfigPath();
        var text = File.ReadAllText(ConfigPath.FullName);
        return WinappConfigDocument.Parse(text).Config;
    }

    public void Save(WinappConfig cfg)
    {
        GuardConfigPath();
        // Full serialization — drops comments / unknown fields.
        var yaml = new WinappConfigDocument(cfg).Render();
        PathSafety.AtomicWriteAllText(ConfigPath.FullName, yaml, Utf8NoBom);
        ConfigPath.Refresh();
    }

    public void SaveJsBindingsOnly(WinappConfig cfg)
    {
        GuardConfigPath();
        string yaml;
        if (ConfigPath.Exists)
        {
            string existing;
            try
            {
                existing = File.ReadAllText(ConfigPath.FullName);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not read existing winapp.yaml at {ConfigPath.FullName} to splice "
                    + "jsBindings while preserving comments. Close any editor/process that may "
                    + "be holding the file open, then retry. "
                    + $"Underlying error: {ex.Message}", ex);
            }

            try
            {
                yaml = new WinappConfigDocument(cfg).SpliceJsBindingsInto(existing);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not splice jsBindings: block into winapp.yaml at {ConfigPath.FullName} "
                    + "without losing comments or unknown fields. The file's structure may be "
                    + "malformed; fix it manually or remove the jsBindings: block and re-run. "
                    + $"Underlying error: {ex.Message}", ex);
            }
        }
        else
        {
            yaml = new WinappConfigDocument(cfg).Render();
        }
        // Atomic write (temp + rename) so a crash mid-write can't leave
        // winapp.yaml truncated. Pairs with reparse-point refusal above to
        // make the path safe end-to-end.
        PathSafety.AtomicWriteAllText(ConfigPath.FullName, yaml, Utf8NoBom);
        ConfigPath.Refresh();
    }

    // Refuse to read or rewrite winapp.yaml if the file (or any directory
    // between it and its config-dir) is a symlink/junction — a malicious
    // workspace could otherwise redirect the I/O at an arbitrary file on
    // disk (e.g. clobbering a victim project's config). The config-dir
    // itself is the containment boundary because `--config-dir` legitimately
    // points outside the base workspace.
    private void GuardConfigPath()
    {
        var boundary = ConfigPath.DirectoryName ?? Directory.GetCurrentDirectory();
        if (PathSafety.HasReparsePointOnPath(ConfigPath.FullName, boundary))
        {
            throw new InvalidOperationException(
                $"Refusing to access '{ConfigPath.FullName}': the file or one of its "
                + "ancestors up to the config directory is a symbolic link / reparse "
                + "point. Resolve the link and re-run.");
        }
    }
}