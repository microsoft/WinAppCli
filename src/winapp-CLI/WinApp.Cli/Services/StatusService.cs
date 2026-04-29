// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Service for managing Spectre.Console status displays with ILogger integration.
/// Uses Spectre.Console Live display for automatic terminal handling.
/// </summary>
internal class StatusService(IAnsiConsole ansiConsole, ILogger<StatusService> logger) : IStatusService
{
    public async Task<int> ExecuteWithStatusAsync<T>(string inProgressMessage, Func<TaskContext, CancellationToken, Task<(int ReturnCode, T CompletedMessage)>> taskFunc, CancellationToken cancellationToken)
    {
        var renderLock = new Lock();
        GroupableTask<(int ReturnCode, T CompletedMessage)> task = new(inProgressMessage, null, taskFunc, ansiConsole, logger, renderLock);

        var useLiveSpinner = ProgressDisplay.ShouldUseLiveSpinner(ansiConsole, logger);
        var infoEnabled = logger.IsEnabled(LogLevel.Information);

        // In plain mode, hook a renderer that prints each task start/finish and status
        // message as it occurs. In live mode the spinner loop handles rendering.
        // When info-level output is suppressed (--quiet/--json), no callback is needed.
        PlainProgressRenderer? plainRenderer = null;
        Action? onUpdate = null;
        if (!useLiveSpinner && infoEnabled)
        {
            plainRenderer = new PlainProgressRenderer(ansiConsole, renderLock, task);
            onUpdate = plainRenderer.OnUpdate;
        }

        var taskExecution = task.ExecuteAsync(onUpdate, cancellationToken);

        IRenderable rendered;

        (int ReturnCode, T CompletedMessage)? result = null;
        if (useLiveSpinner)
        {
            rendered = task.Render();
            // Run the Live display until task completes
            await ansiConsole.Live(rendered)
                .AutoClear(true)
                .Overflow(VerticalOverflow.Crop)
                .Cropping(VerticalOverflowCropping.Top)
                .StartAsync(async ctx =>
                {
                    while (!taskExecution.IsCompleted)
                    {
                        lock (renderLock)
                        {
                            rendered = task.Render();
                            ctx.UpdateTarget(rendered);
                        }
                        ctx.Refresh();

                        // Wait for animation refresh (100ms) or task completion
                        await Task.WhenAny(taskExecution, Task.Delay(100, cancellationToken));
                    }
                });
        }
        else
        {
            // Plain (or silent) mode: the PlainProgressRenderer hooked to onUpdate (when
            // info logging is enabled) streams each line as it happens. Just await here.
            try
            {
                result = await taskExecution;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (!logger.IsEnabled(LogLevel.Error))
            {
                return JsonErrorOutput.Write(ansiConsole, ex.Message);
            }
        }

        if (infoEnabled && useLiveSpinner)
        {
            // Final render to show completed state for the live-spinner path. The plain
            // renderer already streamed completion lines as they arrived.
            lock (renderLock)
            {
                rendered = task.Render(true);
            }

            ansiConsole.Write(rendered);
        }
        else if (infoEnabled && plainRenderer != null)
        {
            // Flush any straggler updates that arrived between the last onUpdate and
            // task completion (e.g., the root task's own success/failure state).
            plainRenderer.OnUpdate();
        }

        // Get the result
        try
        {
            result ??= await taskExecution;
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
        catch (Exception ex) when (!logger.IsEnabled(LogLevel.Error))
        {
            return JsonErrorOutput.Write(ansiConsole, ex.Message);
        }

        if (result != null)
        {
            if (result.Value.ReturnCode != 0)
            {
                // The task returned a non-zero code with an error message.
                // Let the calling command handle JSON error output — it knows its own schema.
                // StatusService only handles unhandled exceptions (caught above).
                if (logger.IsEnabled(LogLevel.Error))
                {
                    logger.LogError("{CompletedMessage}", result.Value.CompletedMessage);
                    if (!logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogInformation("Run with --verbose for more details.");
                    }
                }
            }
            else
            {
                logger.LogDebug("Task completed successfully with message: {CompletedMessage}", result.Value.CompletedMessage);
            }
            return result.Value.ReturnCode;
        }

        return 1;
    }
}
