// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Tests;

/// <summary>
/// Coverage for <see cref="StatusMessageTask"/>, the leaf task the console renderers use to print a
/// one-shot info/verbose/warning status line inside a task group. It is always pre-completed (it has
/// no work to run), so the assertions pin down that immediately-successful contract.
/// </summary>
[TestClass]
public sealed class StatusMessageTaskTests
{
    private static StatusMessageTask CreateTask(string message)
        => new(message, parent: null, new TestConsole(), NullLogger.Instance, new Lock());

    [TestMethod]
    public void Constructor_MarksTaskSuccessfullyCompletedWithMessageAsCompletion()
    {
        var task = CreateTask("ℹ  Restored packages");

        // A status line is informational: it is born already succeeded and renders its own text.
        Assert.AreEqual(true, task.SuccessfullyCompleted);
        Assert.AreEqual("ℹ  Restored packages", task.InProgressMessage);
        Assert.AreEqual("ℹ  Restored packages", task.CompletedMessage);
    }

    [TestMethod]
    public async Task ExecuteAsync_InvokesOnUpdateAndReturnsCompletedMessage()
    {
        var task = CreateTask("ℹ  Downloaded 3 packages");
        var updates = 0;

        var result = await task.ExecuteAsync(() => updates++, CancellationToken.None);

        Assert.AreEqual("ℹ  Downloaded 3 packages", result,
            "ExecuteAsync must surface the pre-set completion message so renderers can print it.");
        Assert.AreEqual(1, updates, "ExecuteAsync must notify the renderer exactly once via onUpdate.");
        Assert.AreEqual(true, task.SuccessfullyCompleted);
    }

    [TestMethod]
    public async Task ExecuteAsync_NullOnUpdate_DoesNotThrowAndStillReturnsMessage()
    {
        var task = CreateTask("ℹ  No callback wired");

        var result = await task.ExecuteAsync(null, CancellationToken.None);

        Assert.AreEqual("ℹ  No callback wired", result);
    }
}
