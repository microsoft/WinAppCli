// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class PromptConfirmationTaskTests
{
    [TestMethod]
    public async Task WaitForInputAsync_YThenEnter_ConfirmsAndShowsYesPrompt()
    {
        var (task, console, updates) = CreateTask("Install package?");
        console.Input.PushKey(ConsoleKey.Y);
        console.Input.PushKey(ConsoleKey.Enter);

        var result = await task.WaitForInputAsync(CancellationToken.None);

        Assert.IsTrue(result);
        Assert.AreEqual(PromptState.Confirmed, task.State);
        Assert.AreEqual(true, task.CompletedMessage);
        Assert.AreEqual(true, task.SuccessfullyCompleted);
        StringAssert.Contains(task.InProgressMessage, "Install package?");
        StringAssert.Contains(task.InProgressMessage, "Yes");
        Assert.IsTrue(updates.Any(message => message.Contains("[green](y)[/]: Y", StringComparison.Ordinal)), string.Join("|", updates));
    }

    [TestMethod]
    public async Task WaitForInputAsync_NThenEnter_DeclinesAndShowsNoPrompt()
    {
        var (task, console, updates) = CreateTask("Continue?");
        console.Input.PushKey(ConsoleKey.N);
        console.Input.PushKey(ConsoleKey.Enter);

        var result = await task.WaitForInputAsync(CancellationToken.None);

        Assert.IsFalse(result);
        Assert.AreEqual(PromptState.Declined, task.State);
        Assert.AreEqual(false, task.CompletedMessage);
        Assert.AreEqual(true, task.SuccessfullyCompleted);
        StringAssert.Contains(task.InProgressMessage, "Continue?");
        StringAssert.Contains(task.InProgressMessage, "No");
        Assert.IsTrue(updates.Any(message => message.Contains("[green](y)[/]: N", StringComparison.Ordinal)), string.Join("|", updates));
    }

    [TestMethod]
    public async Task WaitForInputAsync_EnterWithNoTypedInput_UsesDefaultYes()
    {
        var (task, console, updates) = CreateTask("Use defaults?");
        console.Input.PushKey(ConsoleKey.Enter);

        var result = await task.WaitForInputAsync(CancellationToken.None);

        Assert.IsTrue(result);
        Assert.AreEqual(PromptState.Confirmed, task.State);
        Assert.IsTrue(updates.First().Contains("Use defaults? [blue][[Y/n]][/] [green](y)[/]: ", StringComparison.Ordinal));
        StringAssert.Contains(task.InProgressMessage, "Yes");
    }

    [TestMethod]
    public async Task WaitForInputAsync_InvalidThenValid_IgnoresInvalidKeyAndAcceptsValidAnswer()
    {
        var (task, console, updates) = CreateTask("Overwrite?");
        console.Input.PushKey(ConsoleKey.X);
        console.Input.PushKey(ConsoleKey.N);
        console.Input.PushKey(ConsoleKey.Enter);

        var result = await task.WaitForInputAsync(CancellationToken.None);

        Assert.IsFalse(result);
        Assert.AreEqual(PromptState.Declined, task.State);
        Assert.IsFalse(updates.Any(message => message.Contains(": X", StringComparison.Ordinal)), string.Join("|", updates));
        Assert.IsTrue(updates.Any(message => message.Contains(": N", StringComparison.Ordinal)), string.Join("|", updates));
    }

    [TestMethod]
    public async Task WaitForInputAsync_BackspaceRemovesTypedAnswerBeforeDefaultEnter()
    {
        var (task, console, updates) = CreateTask("Proceed?");
        console.Input.PushKey(ConsoleKey.N);
        console.Input.PushKey(ConsoleKey.Backspace);
        console.Input.PushKey(ConsoleKey.Enter);

        var result = await task.WaitForInputAsync(CancellationToken.None);

        Assert.IsTrue(result);
        Assert.AreEqual(PromptState.Confirmed, task.State);
        Assert.IsTrue(updates.Any(message => message.EndsWith(": N", StringComparison.Ordinal)), string.Join("|", updates));
        Assert.IsTrue(updates.Any(message => message.EndsWith(": ", StringComparison.Ordinal)), string.Join("|", updates));
        StringAssert.Contains(task.InProgressMessage, "Yes");
    }

    [TestMethod]
    public async Task WaitForInputAsync_EscapeDeclinesImmediately()
    {
        var (task, console, _) = CreateTask("Cancel?");
        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushKey(ConsoleKey.Y);
        console.Input.PushKey(ConsoleKey.Enter);

        var result = await task.WaitForInputAsync(CancellationToken.None);

        Assert.IsFalse(result);
        Assert.AreEqual(PromptState.Declined, task.State);
        StringAssert.Contains(task.InProgressMessage, "No");
    }

    [TestMethod]
    public async Task WaitForInputAsync_CancellationBeforeInput_ReturnsFalseAndShowsCancelledPrompt()
    {
        var (task, _, updates) = CreateTask("Wait forever?");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await task.WaitForInputAsync(cts.Token);

        Assert.IsFalse(result);
        Assert.AreEqual(PromptState.Cancelled, task.State);
        Assert.AreEqual(false, task.CompletedMessage);
        StringAssert.Contains(task.InProgressMessage, "Wait forever?");
        StringAssert.Contains(task.InProgressMessage, "(cancelled)");
        Assert.IsTrue(updates.Last().Contains("(cancelled)", StringComparison.Ordinal), string.Join("|", updates));
    }

    [TestMethod]
    public async Task WaitForInputAsync_CancellationWhileWaiting_ReturnsFalseAndShowsCancelledPrompt()
    {
        var (task, _, updates) = CreateTask("Still waiting?");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await task.WaitForInputAsync(cts.Token);

        Assert.IsFalse(result);
        Assert.AreEqual(PromptState.Cancelled, task.State);
        Assert.IsTrue(updates.Last().Contains("(cancelled)", StringComparison.Ordinal), string.Join("|", updates));
    }

    [TestMethod]
    public void FormatPromptMessage_UnknownStateFallsBackToPromptText()
    {
        var method = typeof(PromptConfirmationTask).GetMethod("FormatPromptMessage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var result = method!.Invoke(null, ["Raw prompt", (PromptState)999, "ignored"]);

        Assert.AreEqual("Raw prompt", result);
    }

    [TestMethod]
    public async Task ExecuteAsync_IsNotInputDriverAndReturnsFalseWithoutChangingState()
    {
        var (task, _, _) = CreateTask("Execute?");

        var result = await task.ExecuteAsync(null, CancellationToken.None);

        Assert.IsFalse(result);
        Assert.AreEqual(PromptState.WaitingForInput, task.State);
        Assert.IsNull(task.SuccessfullyCompleted);
    }

    private static (PromptConfirmationTask Task, TestConsole Console, List<string> Updates) CreateTask(string promptText)
    {
        var console = new TestConsole();
        var updates = new List<string>();
        PromptConfirmationTask? task = null;
        task = new PromptConfirmationTask(
            promptText,
            parent: null,
            ansiConsole: console,
            logger: NullLogger.Instance,
            renderLock: new Lock(),
            onUpdate: () => updates.Add(task!.InProgressMessage));
        updates.Add(task.InProgressMessage);
        return (task, console, updates);
    }
}
