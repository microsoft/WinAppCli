// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class WindowsSandboxCliTests
{
    private const string SandboxId = "7cdaf716-6807-4cb6-b23b-f71244b0ee90";

    [TestMethod]
    public async Task ListAsync_UsesRawOutputAndParsesIds()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(
            0,
            $$"""
            {
              "WindowsSandboxEnvironments": [
                { "Id": "{{SandboxId.ToUpperInvariant()}}" }
              ]
            }
            """,
            ""));
        var cli = new WindowsSandboxCli(runner);

        var result = await cli.ListAsync();

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(new[] { SandboxId }, result.Value!.ToArray());
        AssertRequest(runner.Requests.Single(), "list", "--raw");
    }

    [TestMethod]
    public async Task ListAsync_RejectsDuplicateOrMalformedIds()
    {
        var duplicateRunner = new FakeProcessRunner(new ProcessRunResult(
            0,
            $$"""{"WindowsSandboxEnvironments":[{"Id":"{{SandboxId}}"},{"Id":"{{SandboxId}}"}]}""",
            ""));
        var malformedRunner = new FakeProcessRunner(new ProcessRunResult(
            0,
            """{"WindowsSandboxEnvironments":[{"Id":"not-a-guid"}]}""",
            ""));

        var duplicate = await new WindowsSandboxCli(duplicateRunner).ListAsync();
        var malformed = await new WindowsSandboxCli(malformedRunner).ListAsync();

        Assert.AreEqual(WindowsSandboxCliFailure.IncompatibleOutput, duplicate.Failure);
        Assert.AreEqual(WindowsSandboxCliFailure.IncompatibleOutput, malformed.Failure);
    }

    [TestMethod]
    public async Task ListAsync_RejectsNullEnvironmentEntries()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(
            0,
            """{"WindowsSandboxEnvironments":[null]}""",
            ""));

        var result = await new WindowsSandboxCli(runner).ListAsync();

        Assert.AreEqual(WindowsSandboxCliFailure.IncompatibleOutput, result.Failure);
    }

    [TestMethod]
    public async Task StartAsync_UsesRawOutputAndParsesId()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(
            0,
            $$"""{"Id":"{{SandboxId.ToUpperInvariant()}}"}""",
            ""));
        var cli = new WindowsSandboxCli(runner);

        var result = await cli.StartAsync();

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(SandboxId, result.Value);
        AssertRequest(runner.Requests.Single(), "start", "--raw");
    }

    [TestMethod]
    public async Task StopAsync_PreservesValidatedArgumentBoundary()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(0, "{}", ""));
        var cli = new WindowsSandboxCli(runner);

        var result = await cli.StopAsync(SandboxId.ToUpperInvariant());

        Assert.IsTrue(result.Succeeded);
        AssertRequest(runner.Requests.Single(), "stop", "--id", SandboxId, "--raw");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => cli.StopAsync("bad --id"));
    }

    [TestMethod]
    public async Task CommandFailure_ReturnsFailureWithoutParsingOutput()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(12, "not-json", "feature disabled"));
        var cli = new WindowsSandboxCli(runner);

        var result = await cli.ListAsync();

        Assert.AreEqual(WindowsSandboxCliFailure.CommandFailed, result.Failure);
        StringAssert.Contains(result.Error, "feature disabled");
    }

    [TestMethod]
    public async Task MissingExecutable_ReturnsSpecificFailure()
    {
        var runner = new FakeProcessRunner(new Win32Exception(2, "not found"));
        var cli = new WindowsSandboxCli(runner);

        var result = await cli.ListAsync();

        Assert.AreEqual(WindowsSandboxCliFailure.ExecutableMissing, result.Failure);
    }

    [TestMethod]
    public async Task Cancellation_Propagates()
    {
        var runner = new FakeProcessRunner(new OperationCanceledException());
        var cli = new WindowsSandboxCli(runner);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => cli.ListAsync());
    }

    private static void AssertRequest(ProcessRunRequest request, params string[] expectedArguments)
    {
        Assert.AreEqual("wsb.exe", request.FileName);
        CollectionAssert.AreEqual(expectedArguments, request.Arguments.ToArray());
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly ProcessRunResult? _result;
        private readonly Exception? _exception;

        public FakeProcessRunner(ProcessRunResult result)
        {
            _result = result;
        }

        public FakeProcessRunner(Exception exception)
        {
            _exception = exception;
        }

        public List<ProcessRunRequest> Requests { get; } = [];

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            Action<string>? onOutputLine = null,
            Action<string>? onErrorLine = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (_exception is not null)
            {
                return Task.FromException<ProcessRunResult>(_exception);
            }
            return Task.FromResult(_result!);
        }
    }
}
