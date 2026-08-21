// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="TargetStateStore"/>. Every test redirects the state root to a temporary
/// directory so real user state under <c>%LOCALAPPDATA%</c> is never read or written.
/// </summary>
[TestClass]
public class TargetStateStoreTests
{
    private DirectoryInfo _tempRoot = null!;
    private TargetStateStore _store = null!;
    private ExecutionTargetRef _target = null!;

    [TestInitialize]
    public void Setup()
    {
        // An explicit per-test root keeps these tests isolated under the assembly's method-level
        // parallelism, and guarantees real state under %LOCALAPPDATA% is never touched.
        _tempRoot = new DirectoryInfo(TestPaths.TempRoot("TargetState"));
        _tempRoot.Create();

        _store = new TargetStateStore(new TargetStateDirectoryProvider(_tempRoot.FullName));
        _target = ExecutionTargetRef.WindowsSandboxDefault;
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempRoot.Exists)
        {
            _tempRoot.Delete(recursive: true);
        }
    }

    private static TargetState NewState(string? instanceId = "instance-1", string? nonce = "nonce-1") => new()
    {
        SchemaVersion = TargetStateStore.CurrentSchemaVersion,
        Revision = 0,
        TargetKind = ExecutionTargetRef.WindowsSandboxKind,
        TargetId = ExecutionTargetRef.WindowsSandboxDefault.Id,
        InstanceId = instanceId,
        BootNonce = nonce,
    };

    private string StateFilePath =>
        Path.Join(_tempRoot.FullName, _target.Slug, TargetStateStore.StateFileName);

    [TestMethod]
    public void Read_NoState_ReturnsNull() => Assert.IsNull(_store.Read(_target));

    [TestMethod]
    public void Commit_FirstWrite_StartsAtRevisionOne()
    {
        var committed = _store.Commit(_target, NewState(), expectedRevision: 0);

        Assert.AreEqual(1, committed.Revision);
        Assert.AreEqual("instance-1", committed.InstanceId);
        Assert.AreEqual(TargetStateStore.CurrentSchemaVersion, committed.SchemaVersion);
        Assert.IsNotNull(committed.UpdatedUtc);
    }

    [TestMethod]
    public void Commit_UsesTargetSlugDirectory_MatchingSpecPath()
    {
        _store.Commit(_target, NewState(), expectedRevision: 0);

        // The spec pins %LOCALAPPDATA%\Microsoft\WinApp\Targets\windows-sandbox-default.
        Assert.AreEqual("windows-sandbox-default", _target.Slug);
        Assert.IsTrue(File.Exists(StateFilePath), $"Expected state at {StateFilePath}.");
    }

    [TestMethod]
    public void ReadAfterCommit_RoundTripsEveryField()
    {
        var committed = _store.Commit(
            _target,
            NewState() with { AgentVersion = "1.2.3", AgentBinaryHash = "abc123" },
            expectedRevision: 0);

        var read = _store.Read(_target);

        Assert.IsNotNull(read);
        Assert.AreEqual(committed.Revision, read.Revision);
        Assert.AreEqual("instance-1", read.InstanceId);
        Assert.AreEqual("nonce-1", read.BootNonce);
        Assert.AreEqual("1.2.3", read.AgentVersion);
        Assert.AreEqual("abc123", read.AgentBinaryHash);
        Assert.AreEqual(ExecutionTargetRef.WindowsSandboxKind, read.TargetKind);
    }

    [TestMethod]
    public void Commit_IncrementsRevisionMonotonically()
    {
        var first = _store.Commit(_target, NewState(), expectedRevision: 0);
        var second = _store.Commit(_target, NewState(instanceId: "instance-2"), first.Revision);
        var third = _store.Commit(_target, NewState(instanceId: "instance-3"), second.Revision);

        Assert.AreEqual(1, first.Revision);
        Assert.AreEqual(2, second.Revision);
        Assert.AreEqual(3, third.Revision);
        Assert.AreEqual("instance-3", _store.Read(_target)!.InstanceId);
    }

    [TestMethod]
    public void Commit_StaleRevision_FailsClosedWithoutOverwriting()
    {
        var first = _store.Commit(_target, NewState(), expectedRevision: 0);
        _store.Commit(_target, NewState(instanceId: "winner"), first.Revision);

        // A second host process still holding the old revision must not clobber the winner.
        var exception = Assert.ThrowsExactly<ExecutionTargetException>(
            () => _store.Commit(_target, NewState(instanceId: "loser"), first.Revision));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, exception.Error.Code);
        Assert.AreEqual("winner", _store.Read(_target)!.InstanceId, "The losing commit must not apply.");
    }

    [TestMethod]
    public void Read_CorruptState_FailsClosed()
    {
        _store.Commit(_target, NewState(), expectedRevision: 0);
        File.WriteAllText(StateFilePath, "{ this is not valid json");

        var exception = Assert.ThrowsExactly<ExecutionTargetException>(() => _store.Read(_target));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, exception.Error.Code);
        Assert.IsNotNull(exception.Error.UserAction, "A corrupt-state failure must tell the user how to recover.");
    }

    [TestMethod]
    public void Read_NewerSchema_FailsClosedAndAsksForAnUpdate()
    {
        _store.Commit(_target, NewState(), expectedRevision: 0);
        var newer = File.ReadAllText(StateFilePath)
            .Replace($"\"schemaVersion\": {TargetStateStore.CurrentSchemaVersion}", "\"schemaVersion\": 9999", StringComparison.Ordinal);
        File.WriteAllText(StateFilePath, newer);

        var exception = Assert.ThrowsExactly<ExecutionTargetException>(() => _store.Read(_target));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, exception.Error.Code);
        StringAssert.Contains(exception.Error.UserAction!, "Update winapp", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void Clear_RemovesState_AndIsIdempotent()
    {
        _store.Commit(_target, NewState(), expectedRevision: 0);

        _store.Clear(_target);
        Assert.IsNull(_store.Read(_target));

        _store.Clear(_target);
        Assert.IsNull(_store.Read(_target));
    }

    [TestMethod]
    public void Commit_LeavesNoTemporaryFilesBehind()
    {
        _store.Commit(_target, NewState(), expectedRevision: 0);

        var files = Directory.GetFiles(Path.Join(_tempRoot.FullName, _target.Slug));

        Assert.AreEqual(1, files.Length, "Atomic replace must not leave temporary files behind.");
        Assert.AreEqual(TargetStateStore.StateFileName, Path.GetFileName(files[0]));
    }

    [TestMethod]
    public void Slug_SanitizesSeparatorsForFilesystemAndKernelNames()
    {
        Assert.AreEqual("windows-sandbox-default", new ExecutionTargetRef("windows-sandbox", "windows-sandbox:default").Slug);
        Assert.AreEqual("hyperv-winui-test", new ExecutionTargetRef("hyperv", "hyperv:winui/test").Slug);
        Assert.AreEqual("target", new ExecutionTargetRef("odd", ":::").Slug);
    }

    [TestMethod]
    public void Epoch_CombinesInstanceAndBootNonce()
    {
        var first = ExecutionTargetEpoch.Create("instance-1", "nonce-a");
        var rebooted = ExecutionTargetEpoch.Create("instance-1", "nonce-b");

        // A provider that reuses instance IDs must still produce a distinct epoch per boot,
        // otherwise stale PID/HWND rejection would silently pass.
        Assert.AreNotEqual(first, rebooted);
        Assert.IsFalse(first.IsNone);
        Assert.IsTrue(ExecutionTargetEpoch.None.IsNone);
    }
}
