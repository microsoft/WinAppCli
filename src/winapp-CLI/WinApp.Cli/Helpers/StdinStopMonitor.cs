// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>Stops a recording on redirected-stdin newline or EOF after capture is ready.</summary>
internal static class StdinStopMonitor
{
    public static void Start(TextReader stdin, Task readyTask, Action stop)
    {
        var t = new Thread(() => MonitorCore(stdin, readyTask, stop))
        {
            IsBackground = true,
            Name = "StdinStopMonitor",
        };
        t.Start();
    }

    internal static void MonitorCore(TextReader stdin, Task readyTask, Action stop)
    {
        try
        {
            stdin.ReadLine();
        }
        catch
        {
            // Treat stdin failures as EOF.
        }

        readyTask.GetAwaiter().GetResult();
        stop();
    }
}
