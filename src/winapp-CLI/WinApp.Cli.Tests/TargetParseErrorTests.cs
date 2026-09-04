// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.Tests;

/// <summary>
/// A <c>winapp target</c> command line the parser rejects must fail in the target error contract,
/// not in System.CommandLine's usage help.
/// </summary>
/// <remarks>
/// <para>
/// These verbs exist for agents and scripts. A caller that asks for <c>--json</c> and receives a
/// page of help text has no code to branch on and nothing to parse, so it either crashes on the
/// first <c>JSON.parse</c> or — worse — treats an unparseable answer as no answer and carries on.
/// </para>
/// <para>
/// The failures covered here all happen <em>before</em> the handler that owns the error envelope
/// runs: a required argument is missing, or an option's value cannot be converted. Nothing is
/// prepared, no target is contacted, and no Sandbox is involved.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public class TargetParseErrorTests
{
    private static Task<(string Stdout, string Stderr, int ExitCode)> InvokeAsync(params string[] args) =>
        ProgramMainTestHarness.InvokeProgramAsync(args);

    private static JsonElement ParseEnvelope(string stderr)
    {
        var start = stderr.IndexOf('{', StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"stderr must carry a JSON error envelope; got: {stderr}");

        return JsonSerializer.Deserialize<JsonElement>(stderr.AsSpan(start).TrimEnd());
    }

    private static void AssertTargetEnvelope(string stdout, string stderr, int exitCode)
    {
        Assert.AreEqual(
            string.Empty,
            stdout.Trim(),
            "The target verbs put every envelope on stderr; stdout belongs to the target command.");

        var envelope = ParseEnvelope(stderr);

        Assert.AreEqual(
            ExecutionTargetErrorCodes.TargetInvalid,
            envelope.GetProperty("error").GetProperty("code").GetString(),
            "A command line winapp could not parse names no usable target.");

        Assert.IsFalse(
            string.IsNullOrWhiteSpace(envelope.GetProperty("error").GetProperty("message").GetString()),
            "The envelope must say what was actually wrong.");

        Assert.AreEqual(
            1,
            exitCode,
            "A malformed command line exits 1, like every other one — not the code that means the " +
            "target could not be reached.");
    }

    /// <summary>An exec with no command after <c>--</c> is refused as structured JSON.</summary>
    /// <remarks>
    /// The likeliest mistake with this verb: the separator is there but the command was dropped, or
    /// a shell consumed it.
    /// </remarks>
    [TestMethod]
    public async Task Exec_WithNoCommand_Json_EmitsTheTargetEnvelope()
    {
        var (stdout, stderr, exitCode) = await InvokeAsync("target", "exec", "sandbox", "--json");

        AssertTargetEnvelope(stdout, stderr, exitCode);
    }

    /// <summary>An exec with no selector at all is refused as structured JSON.</summary>
    [TestMethod]
    public async Task Exec_WithNoSelector_Json_EmitsTheTargetEnvelope()
    {
        var (stdout, stderr, exitCode) = await InvokeAsync("target", "exec", "--json");

        AssertTargetEnvelope(stdout, stderr, exitCode);
    }

    /// <summary>A push missing its destination is refused as structured JSON.</summary>
    /// <remarks>
    /// Worth refusing loudly: the parser binds the source to the <em>selector</em> in this shape, so
    /// silently proceeding would act on a target named after the user's file.
    /// </remarks>
    [TestMethod]
    public async Task Push_WithNoDestination_Json_EmitsTheTargetEnvelope()
    {
        var (stdout, stderr, exitCode) = await InvokeAsync("target", "push", "sandbox", "setup.ps1", "--json");

        AssertTargetEnvelope(stdout, stderr, exitCode);
    }

    /// <summary>A pull missing its destination is refused as structured JSON.</summary>
    [TestMethod]
    public async Task Pull_WithNoDestination_Json_EmitsTheTargetEnvelope()
    {
        var (stdout, stderr, exitCode) = await InvokeAsync("target", "pull", "sandbox", "logs", "--json");

        AssertTargetEnvelope(stdout, stderr, exitCode);
    }

    /// <summary>An unparseable option value is refused as structured JSON.</summary>
    /// <remarks>
    /// <c>--json=maybe</c> is rejected before the command runs. Reported in the target envelope
    /// rather than as a bare line, because a caller asking for machine-readable output has said what
    /// it can read.
    /// </remarks>
    [TestMethod]
    public async Task Exec_WithAnUnparseableOptionValue_Json_EmitsTheTargetEnvelope()
    {
        var (stdout, stderr, exitCode) = await InvokeAsync(
            "target", "exec", "sandbox", "--json=maybe", "--", "cmd.exe");

        AssertTargetEnvelope(stdout, stderr, exitCode);
    }

    /// <summary>
    /// A misspelt option alongside a valid <c>--json</c> is refused as structured JSON.
    /// </summary>
    /// <remarks>
    /// System.CommandLine binds an unrecognised token to a nearby positional rather than failing, so
    /// this is caught before dispatch. Reported in the target envelope because the caller did ask
    /// for machine-readable output, and that is what it can read.
    /// </remarks>
    [TestMethod]
    public async Task Exec_WithAMisspeltOption_Json_EmitsTheTargetEnvelope()
    {
        var (stdout, stderr, exitCode) = await InvokeAsync(
            "target", "exec", "sandbox", "--json", "-cwd", @"C:\", "--", "cmd.exe");

        AssertTargetEnvelope(stdout, stderr, exitCode);
    }

    /// <summary>
    /// A mistyped <c>--json</c> itself stays human-readable, because JSON was never requested.
    /// </summary>
    /// <remarks>
    /// <c>-json</c> is not <c>--json</c>, so nothing has told winapp the caller can read a document.
    /// Answering in prose — and saying that single-dash flags are reserved for short aliases — is
    /// what points at the actual mistake, which emitting an envelope would bury.
    /// </remarks>
    [TestMethod]
    public async Task Exec_WithAMistypedJsonFlag_StaysHumanReadable()
    {
        var (stdout, stderr, exitCode) = await InvokeAsync(
            "target", "exec", "sandbox", "-json", "--", "cmd.exe");

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(string.Empty, stdout.Trim());
        StringAssert.Contains(stderr, "-json", "The message must name the token the user actually typed.");

        Assert.IsFalse(
            stderr.Contains("\"error\"", StringComparison.Ordinal),
            "JSON was never requested, so answering with a document would guess at what the caller wanted.");
    }

    /// <summary>
    /// Without <c>--json</c> the same mistakes stay human-readable, with no JSON on any stream.
    /// </summary>
    /// <remarks>
    /// The bridge exists for machine callers only. Imposing an envelope on a person at a terminal
    /// would replace the parser's usage help — which names the missing argument — with a document
    /// they then have to read past.
    /// </remarks>
    [TestMethod]
    public async Task Exec_WithNoCommand_WithoutJson_StaysHumanReadable()
    {
        var (stdout, stderr, exitCode) = await InvokeAsync("target", "exec", "sandbox");

        Assert.AreNotEqual(0, exitCode, "A command line missing a required argument must not succeed.");

        Assert.IsFalse(
            stdout.Contains("\"error\"", StringComparison.Ordinal) ||
            stderr.Contains("\"error\"", StringComparison.Ordinal),
            "A human gets prose, not an envelope.");
    }

    /// <summary>
    /// The bridge is scoped to the target tree and never reshapes another command's contract.
    /// </summary>
    /// <remarks>
    /// <c>ui</c>, <c>new</c>, and <c>find-ui</c> each document a different error shape, and several
    /// of them also accept <c>--on</c>. Keying the bridge on the command tree rather than on that
    /// option is what keeps this one envelope from leaking into theirs.
    /// </remarks>
    [TestMethod]
    public async Task UiParseError_Json_KeepsTheUiEnvelopeNotTheTargetOne()
    {
        var (_, stderr, _) = await InvokeAsync(
            "ui", "pen", "-a", "TestApp", "--at", "100,100", "--pressure", "nope", "--json");

        var envelope = ParseEnvelope(stderr);

        Assert.AreEqual(
            WinApp.Cli.Helpers.UiJsonError.CodeInvalidArguments,
            envelope.GetProperty("error").GetProperty("code").GetString(),
            "The ui contract must be unchanged by the target bridge.");
    }
}
