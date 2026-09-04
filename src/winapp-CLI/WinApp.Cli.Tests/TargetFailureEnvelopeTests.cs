// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// A command that could not reach its target must still answer in the shape its own <c>--json</c>
/// contract promises.
/// </summary>
/// <remarks>
/// <para>
/// <c>winapp run --json</c> and <c>winapp unregister --json</c> each publish exactly one documented
/// object on stdout, and that is where a scripted caller looks. Adding <c>--on &lt;target&gt;</c>
/// must not move that answer somewhere else: a caller whose stdout is empty — but only when a
/// target was involved, and only when something failed — has to write a second parser for a second
/// stream to find out why, or more likely reports success because the document it expected was not
/// there to contradict it.
/// </para>
/// <para>
/// Every failure here is a real one raised by the target machinery: the backend reports the host
/// cannot run the target at all, which is the same path a machine without Windows Sandbox installed,
/// without the optional feature enabled, or with a broken connection takes.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public class TargetFailureEnvelopeTests : BaseCommandTests
{
    private const string TestManifestContent = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
          <Identity Name="TargetEnvelopeTestPackage" Publisher="CN=TargetEnvelopeTests" Version="1.0.0.0" />
          <Properties>
            <DisplayName>Target Envelope Test</DisplayName>
            <PublisherDisplayName>Tests</PublisherDisplayName>
            <Logo>Assets\Logo.png</Logo>
          </Properties>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.18362.0" MaxVersionTested="10.0.26100.0" />
          </Dependencies>
          <Applications>
            <Application Id="TestApp" Executable="TestApp.exe" EntryPoint="TestApp.App">
              <uap:VisualElements DisplayName="Test App" Description="Test application"
                                  BackgroundColor="#777777" Square150x150Logo="Assets\Logo.png" Square44x44Logo="Assets\Logo.png" />
            </Application>
          </Applications>
        </Package>
        """;

    private UnavailableTargetBackend _backend = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _backend = new UnavailableTargetBackend();

        // Materialization is real work on this machine and is not what is under test here: the
        // failure being pinned happens after the layout is prepared, when the target itself turns
        // out to be unusable. Faking it keeps the test to that one step.
        return services
            .AddSingleton<IMsixService>(new FakeMsixService())
            .AddSingleton<IExecutionTargetBackend>(_backend);
    }

    private async Task WriteManifestAsync() => await File.WriteAllTextAsync(
        Path.Join(_tempDirectory.FullName, "appxmanifest.xml"), TestManifestContent, TestContext.CancellationToken);

    /// <summary>
    /// Runs a command line through the root, with the process-wide standard error captured.
    /// </summary>
    /// <remarks>
    /// Parsed from the root because <c>--on</c> is registered there, recursively, rather than on the
    /// target-aware commands themselves — so a command line that omits the root is not the one a
    /// user types.
    /// <para>
    /// Standard error is captured directly because the structured target envelope is written to the
    /// real stream rather than through the injected console: it has to reach stderr even when a
    /// command owns stdout.
    /// </para>
    /// </remarks>
    private async Task<(int ExitCode, string StandardError)> InvokeCapturingStandardErrorAsync(string[] args)
    {
        var original = Console.Error;
        var captured = new StringWriter();

        try
        {
            Console.SetError(captured);
            var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<WinAppRootCommand>(), args);
            return (exitCode, captured.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    private static JsonElement ParseSingleDocument(string text, string what)
    {
        var start = text.IndexOf('{', StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"Expected a JSON document in {what}; got: {text}");

        return JsonSerializer.Deserialize<JsonElement>(text.AsSpan(start).TrimEnd());
    }

    /// <summary>
    /// A target that cannot be prepared still produces <c>unregister</c>'s own result on stdout.
    /// </summary>
    [TestMethod]
    public async Task Unregister_OnAnUnavailableTarget_PublishesItsOwnResultOnStdout()
    {
        await WriteManifestAsync();

        var (exitCode, _) = await InvokeCapturingStandardErrorAsync(
            ["unregister", "--on", "sandbox", "--json"]);

        var result = ParseSingleDocument(TestAnsiConsole.Output, "unregister's stdout");

        Assert.IsTrue(
            result.TryGetProperty("Error", out var error),
            "unregister --json reports a failure through its own 'Error' member; a caller that finds " +
            "no document at all cannot tell a failure from a package that was not registered.");

        Assert.AreEqual(_backend.Failure.Message, error.GetString());
        Assert.AreNotEqual(0, exitCode);
    }

    /// <summary>
    /// The structured target detail is still reported, on the stream that cannot corrupt stdout.
    /// </summary>
    /// <remarks>
    /// The command's own result carries a message and nothing more, so the code a caller branches on
    /// and the recovery advice a person needs would be lost if this were dropped.
    /// </remarks>
    [TestMethod]
    public async Task Unregister_OnAnUnavailableTarget_StillReportsTheStructuredDetailOnStderr()
    {
        await WriteManifestAsync();

        var (_, standardError) = await InvokeCapturingStandardErrorAsync(
            ["unregister", "--on", "sandbox", "--json"]);

        var envelope = ParseSingleDocument(standardError, "unregister's stderr");

        Assert.AreEqual(
            ExecutionTargetErrorCodes.Unsupported,
            envelope.GetProperty("error").GetProperty("code").GetString());

        Assert.AreEqual(
            _backend.Failure.UserAction,
            envelope.GetProperty("error").GetProperty("userAction").GetString());
    }

    /// <summary>Without <c>--json</c>, the failure is plain text on standard error.</summary>
    /// <remarks>
    /// Standard output stays empty in this mode too, so a person piping the command sees the reason
    /// on their terminal rather than mixed into whatever they were collecting.
    /// </remarks>
    [TestMethod]
    public async Task Unregister_OnAnUnavailableTarget_WithoutJson_ReportsPlainTextOnStderr()
    {
        await WriteManifestAsync();

        var (exitCode, standardError) = await InvokeCapturingStandardErrorAsync(
            ["unregister", "--on", "sandbox"]);

        StringAssert.Contains(standardError, _backend.Failure.Message);
        StringAssert.Contains(standardError, _backend.Failure.UserAction);
        Assert.IsFalse(
            standardError.Contains('{', StringComparison.Ordinal),
            "Without --json the failure is prose, not a document.");

        Assert.AreNotEqual(0, exitCode);
    }

    /// <summary>
    /// A target that cannot be prepared still produces <c>run</c>'s own result on stdout.
    /// </summary>
    /// <remarks>
    /// The failure arrives after the application layout is prepared on this machine, so this is the
    /// realistic ordering: the build succeeded, and only the target was unusable.
    /// </remarks>
    [TestMethod]
    public async Task Run_OnAnUnavailableTarget_PublishesItsOwnResultOnStdout()
    {
        await WriteManifestAsync();

        var (exitCode, standardError) = await InvokeCapturingStandardErrorAsync(
            ["run", _tempDirectory.FullName, "--on", "sandbox", "--no-launch", "--json"]);

        var result = ParseSingleDocument(TestAnsiConsole.Output, "run's stdout");

        Assert.IsTrue(
            result.TryGetProperty("Error", out var error),
            "run --json reports every failure through its own result object, and a target failure is " +
            "no different to the caller parsing it.");

        Assert.IsFalse(string.IsNullOrWhiteSpace(error.GetString()));
        Assert.AreNotEqual(0, exitCode);

        // The structured detail is additive, on the stream that cannot disturb that result.
        var envelope = ParseSingleDocument(standardError, "run's stderr");
        Assert.IsTrue(envelope.GetProperty("error").TryGetProperty("code", out _));
    }

    /// <summary>
    /// A target that starts but cannot be reached also answers in <c>run</c>'s own shape.
    /// </summary>
    /// <remarks>
    /// This is the later half of the same contract, and it is a different code path: the host
    /// supports the target, so the fast-fail probe passes and the failure happens while connecting.
    /// A caller must not have to know which of the two it hit to find out what happened.
    /// </remarks>
    [TestMethod]
    public async Task Run_WhenTheTargetCannotBeReached_PublishesItsOwnResultOnStdout()
    {
        await WriteManifestAsync();
        _backend.ProbeFails = false;

        var (exitCode, standardError) = await InvokeCapturingStandardErrorAsync(
            ["run", _tempDirectory.FullName, "--on", "sandbox", "--no-launch", "--json"]);

        var result = ParseSingleDocument(TestAnsiConsole.Output, "run's stdout");

        Assert.AreEqual(_backend.ConnectionFailure.Message, result.GetProperty("Error").GetString());
        Assert.AreEqual(TargetOutput.TargetInfrastructureExitCode, exitCode);

        var envelope = ParseSingleDocument(standardError, "run's stderr");
        Assert.AreEqual(
            ExecutionTargetErrorCodes.TransportFailed,
            envelope.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>
    /// A target that cannot be reached also answers in <c>unregister</c>'s own shape.
    /// </summary>
    [TestMethod]
    public async Task Unregister_WhenTheTargetCannotBeReached_PublishesItsOwnResultOnStdout()
    {
        await WriteManifestAsync();
        _backend.ProbeFails = false;

        var (exitCode, _) = await InvokeCapturingStandardErrorAsync(
            ["unregister", "--on", "sandbox", "--json"]);

        var result = ParseSingleDocument(TestAnsiConsole.Output, "unregister's stdout");

        Assert.AreEqual(_backend.ConnectionFailure.Message, result.GetProperty("Error").GetString());
        Assert.AreEqual(TargetOutput.TargetInfrastructureExitCode, exitCode);
    }

    /// <summary>
    /// The generic <c>target</c> verbs keep their own contract: the envelope on stderr, nothing on
    /// stdout.
    /// </summary>
    [TestMethod]
    public async Task TargetExec_OnAnUnavailableTarget_KeepsTheEnvelopeOnStderrOnly()
    {
        var (exitCode, standardError) = await InvokeCapturingStandardErrorAsync(
            ["target", "exec", "sandbox", "--json", "--", "cmd.exe", "/c", "exit"]);

        Assert.AreEqual(
            string.Empty,
            TestAnsiConsole.Output.Trim(),
            "target exec relays the target command's own output; winapp must not add to it.");

        var envelope = ParseSingleDocument(standardError, "target exec's stderr");
        Assert.AreEqual(
            ExecutionTargetErrorCodes.Unsupported,
            envelope.GetProperty("error").GetProperty("code").GetString());

        Assert.AreEqual(
            TargetOutput.TargetInfrastructureExitCode,
            exitCode,
            "An unreachable target is distinguishable from a command that ran and failed.");
    }

    /// <summary>A backend whose host cannot run the target at all.</summary>
    /// <remarks>
    /// Two distinct failures, because a command reaches them at different points and both must
    /// answer in the same shape. The probe failure is every prerequisite problem — the optional
    /// feature disabled, an unsupported edition, a policy block — caught before anything is built.
    /// The connection failure is everything after that: the target could not be started, or the
    /// channel to it could not be established or was lost.
    /// </remarks>
    private sealed class UnavailableTargetBackend : IExecutionTargetBackend
    {
        /// <summary>When false, the probe passes and the connection is what fails.</summary>
        public bool ProbeFails { get; set; } = true;

        public ExecutionTargetErrorInfo Failure { get; } = new()
        {
            Code = ExecutionTargetErrorCodes.Unsupported,
            Message = "Windows Sandbox is not available on this machine.",
            UserAction = "Enable the Windows Sandbox optional feature, then run the command again.",
        };

        public ExecutionTargetErrorInfo ConnectionFailure { get; } = new()
        {
            Code = ExecutionTargetErrorCodes.TransportFailed,
            Message = "The connection to the guest was lost.",
            UserAction = "Retry the command.",
        };

        public ExecutionTargetRef Target => WindowsSandboxTarget.Default;

        public Task<TargetSupportResult> ProbeSupportAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ProbeFails ? TargetSupportResult.Unsupported(Failure) : TargetSupportResult.Supported);

        public Task<TargetConnection> EnsureConnectedAsync(
            EnsureTargetOptions options,
            CancellationToken cancellationToken) =>
            throw new ExecutionTargetException(ProbeFails ? Failure : ConnectionFailure);

        public IReadOnlyDictionary<string, string> DescribeForDiagnostics() =>
            new Dictionary<string, string>();
    }
}
