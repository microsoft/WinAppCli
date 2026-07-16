// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    [TestCategory("Interactive")]
    [TestCategory("UiRecord")]
    public async Task Record_WebView2StyleCompositedContent_InteractiveCapturesFrames()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("WINAPP_RECORD_INTERACTIVE_TESTS"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set WINAPP_RECORD_INTERACTIVE_TESTS=1 on an interactive desktop lane to run composited-content record coverage.");
        }

        var edgePath = FindEdgePath();
        if (edgePath is null)
        {
            Assert.Inconclusive("Microsoft Edge/WebView2 runtime was not found on this machine.");
        }

        var title = "WinAppRecordCompositedContent";
        var html = Uri.EscapeDataString($$"""
            <!doctype html><title>{{title}}</title>
            <style>
              html,body { margin:0; width:100%; height:100%; overflow:hidden; background:#102040; }
              #box { width:240px; height:160px; margin:40px; background:linear-gradient(135deg,#ff0033,#00ccff); transform:translateZ(0); }
            </style>
            <div id="box"></div>
            """);
        var profileDir = _tempDirectory.CreateSubdirectory("edge-profile").FullName;
        using var browser = Process.Start(new ProcessStartInfo(edgePath)
        {
            UseShellExecute = false,
            ArgumentList =
            {
                "--new-window",
                "--no-first-run",
                "--disable-features=msEdgeLockSettings",
                $"--user-data-dir={profileDir}",
                $"data:text/html,{html}",
            },
        });
        Assert.IsNotNull(browser, "browser process must start");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            var outputPath = Path.Combine(_tempDirectory.FullName, "webview-composited.mp4");
            var result = await RunProcessAsync(
                Path.Combine(AppContext.BaseDirectory, "winapp.exe"),
                ["ui", "record", "--app", title, "--duration-sec", "1", "--fps", "2", "--max-edge", "320", "--output", outputPath, "--json"],
                TimeSpan.FromSeconds(20));

            Assert.AreEqual(0, result.ExitCode, $"ui record failed. stdout={result.Stdout} stderr={result.Stderr}");
            var json = JsonSerializer.Deserialize<JsonElement>(result.Stdout);
            Assert.IsTrue(json.GetProperty("frames").GetInt32() > 0, "record must capture at least one composited frame");
            Assert.IsTrue(new FileInfo(outputPath).Length > 2048, "recorded MP4 should contain non-empty encoded video data");
        }
        finally
        {
            if (!browser.HasExited)
            {
                browser.Kill(entireProcessTree: true);
            }
        }
    }

    private static string? FindEdgePath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"Process timed out: {fileName} {string.Join(" ", arguments)}");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
