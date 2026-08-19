// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

[TestClass]
public class WindowsSandboxStateStoreTests
{
    private DirectoryInfo _tempDirectory = null!;
    private WindowsSandboxStateStore _store = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempDirectory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "winapp-wsb-state-tests", Guid.NewGuid().ToString("N")));
        _store = new WindowsSandboxStateStore(new FakeStateDirectoryProvider(_tempDirectory));
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            _tempDirectory.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of the per-test temporary directory.
        }
    }

    [TestMethod]
    public async Task ReadAsync_WhenMissing_ReturnsMissing()
    {
        var result = await _store.ReadAsync(TestContext.CancellationToken);

        Assert.AreEqual(WindowsSandboxStateReadStatus.Missing, result.Status);
        Assert.IsNull(result.State);
    }

    [TestMethod]
    public async Task WriteAndReadAsync_RoundTripsVersionedState()
    {
        var expected = CreateState("sandbox-1", "0123456789abcdef0123456789abcdef", 7);

        await _store.WriteAsync(expected, TestContext.CancellationToken);
        var result = await _store.ReadAsync(TestContext.CancellationToken);

        Assert.AreEqual(WindowsSandboxStateReadStatus.Valid, result.Status);
        Assert.IsNotNull(result.State);
        Assert.AreEqual(expected.ProviderInstanceId, result.State.ProviderInstanceId);
        Assert.AreEqual(expected.Epoch, result.State.Epoch);
        Assert.AreEqual(expected.Revision, result.State.Revision);

        var json = await File.ReadAllTextAsync(_store.GetStateFile().FullName, TestContext.CancellationToken);
        StringAssert.Contains(json, "\"provider_instance_id\"");
        StringAssert.Contains(json, "\"schema\": 1");
        Assert.IsTrue(json.EndsWith('\n'), "State JSON must end with a line feed.");
        Assert.AreEqual(0, Directory.GetFiles(_store.GetStateFile().DirectoryName!, "*.tmp-*").Length);
    }

    [TestMethod]
    public async Task WriteAsync_AtomicallyReplacesExistingState()
    {
        await _store.WriteAsync(
            CreateState("sandbox-1", "0123456789abcdef0123456789abcdef", 1),
            TestContext.CancellationToken);
        await _store.WriteAsync(
            CreateState("sandbox-2", "abcdef0123456789abcdef0123456789", 2),
            TestContext.CancellationToken);

        var result = await _store.ReadAsync(TestContext.CancellationToken);

        Assert.AreEqual(WindowsSandboxStateReadStatus.Valid, result.Status);
        Assert.AreEqual("sandbox-2", result.State!.ProviderInstanceId);
        Assert.AreEqual(2, result.State.Revision);
    }

    [TestMethod]
    public async Task ReadAsync_WhenJsonIsCorrupt_ReturnsCorrupt()
    {
        var path = _store.GetStateFile();
        path.Directory!.Create();
        await File.WriteAllTextAsync(path.FullName, "{not-json", TestContext.CancellationToken);

        var result = await _store.ReadAsync(TestContext.CancellationToken);

        Assert.AreEqual(WindowsSandboxStateReadStatus.Corrupt, result.Status);
        Assert.IsNull(result.State);
    }

    [TestMethod]
    public async Task ReadAsync_WhenSchemaMetadataIsMissing_ReturnsCorrupt()
    {
        await WriteRawStateAsync(
            """
            {
              "target_id": "windows-sandbox:default",
              "provider_instance_id": "sandbox-1",
              "epoch": "0123456789abcdef0123456789abcdef",
              "revision": 1,
              "created_at_utc": "2026-08-19T12:00:00Z"
            }
            """);

        var result = await _store.ReadAsync(TestContext.CancellationToken);

        Assert.AreEqual(WindowsSandboxStateReadStatus.Corrupt, result.Status);
        Assert.IsNull(result.State);
    }

    [TestMethod]
    public async Task ReadAsync_WhenTargetMetadataIsMissing_ReturnsCorrupt()
    {
        await WriteRawStateAsync(
            """
            {
              "schema": 1,
              "provider_instance_id": "sandbox-1",
              "epoch": "0123456789abcdef0123456789abcdef",
              "revision": 1,
              "created_at_utc": "2026-08-19T12:00:00Z"
            }
            """);

        var result = await _store.ReadAsync(TestContext.CancellationToken);

        Assert.AreEqual(WindowsSandboxStateReadStatus.Corrupt, result.Status);
        Assert.IsNull(result.State);
    }

    [TestMethod]
    public async Task ReadAsync_WhenSchemaIsNewer_ReturnsUnsupportedVersion()
    {
        var path = _store.GetStateFile();
        path.Directory!.Create();
        await File.WriteAllTextAsync(
            path.FullName,
            """
            {
              "schema": 999,
              "target_id": "windows-sandbox:default",
              "provider_instance_id": "sandbox-1",
              "epoch": "0123456789abcdef0123456789abcdef",
              "revision": 1,
              "created_at_utc": "2026-08-19T12:00:00Z"
            }
            """,
            TestContext.CancellationToken);

        var result = await _store.ReadAsync(TestContext.CancellationToken);

        Assert.AreEqual(WindowsSandboxStateReadStatus.UnsupportedVersion, result.Status);
        Assert.IsNull(result.State);
    }

    [TestMethod]
    public async Task ReadAsync_WhenRevisionIsMaximum_ReturnsValidForFailClosedReconciliation()
    {
        var path = _store.GetStateFile();
        path.Directory!.Create();
        await File.WriteAllTextAsync(
            path.FullName,
            $$"""
            {
              "schema": 1,
              "target_id": "windows-sandbox:default",
              "provider_instance_id": "sandbox-1",
              "epoch": "0123456789abcdef0123456789abcdef",
              "revision": {{long.MaxValue}},
              "created_at_utc": "2026-08-19T12:00:00Z"
            }
            """,
            TestContext.CancellationToken);

        var result = await _store.ReadAsync(TestContext.CancellationToken);

        Assert.AreEqual(WindowsSandboxStateReadStatus.Valid, result.Status);
        Assert.AreEqual(long.MaxValue, result.State!.Revision);
    }

    [TestMethod]
    public async Task ReadAsync_WhenMissingThroughJunction_ReturnsUnsafePath()
    {
        var realDirectory = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "real-target"));
        var targetDirectory = Path.Combine(_tempDirectory.FullName, "windows-sandbox-default");
        if (!TryCreateJunction(targetDirectory, realDirectory.FullName))
        {
            Assert.Inconclusive("Could not create a junction.");
            return;
        }

        try
        {
            var result = await _store.ReadAsync(TestContext.CancellationToken);

            Assert.AreEqual(WindowsSandboxStateReadStatus.UnsafePath, result.Status);
            Assert.IsNull(result.State);
        }
        finally
        {
            try
            {
                Directory.Delete(targetDirectory, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of the test junction.
            }
        }
    }

    [TestMethod]
    public async Task WriteAsync_RejectsMalformedStateBeforeWriting()
    {
        var state = CreateState("", "bad-epoch", 0);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => _store.WriteAsync(state, TestContext.CancellationToken));
        Assert.IsFalse(_store.GetStateFile().Exists);
    }

    private static WindowsSandboxTargetState CreateState(string instanceId, string epoch, long revision) =>
        new()
        {
            Schema = WindowsSandboxTargetState.CurrentSchema,
            TargetId = ExecutionTargetRef.WindowsSandboxDefaultId,
            ProviderInstanceId = instanceId,
            Epoch = epoch,
            Revision = revision,
            CreatedAtUtc = "2026-08-19T12:00:00Z",
        };

    private async Task WriteRawStateAsync(string json)
    {
        var path = _store.GetStateFile();
        path.Directory!.Create();
        await File.WriteAllTextAsync(path.FullName, json, TestContext.CancellationToken);
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }

    private sealed class FakeStateDirectoryProvider(DirectoryInfo root) : IExecutionTargetStateDirectoryProvider
    {
        public DirectoryInfo GetStateRoot() => root;

        public DirectoryInfo GetTargetDirectory(ExecutionTargetRef target) =>
            new(Path.Combine(root.FullName, target.Id.Replace(':', '-')));
    }
}
