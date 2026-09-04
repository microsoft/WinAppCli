// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for the heartbeat a running guest agent publishes about itself.
/// </summary>
/// <remarks>
/// The heartbeat is how the host tells a healthy agent from one that started but refused to serve,
/// and it arrives through a guest-writable folder the spec treats as untrusted. Both the "publish
/// the reason" and the "never throw on garbage" behaviours are therefore tested directly.
/// </remarks>
[TestClass]
public class GuestAgentHeartbeatTests
{
    private const string HostHash = "1111111111111111111111111111111111111111111111111111111111111111";

    private static GuestAgentIdentity Host(string version = "1.2.0", string hash = HostHash) =>
        new(version, hash, "arm64", GuestProtocol.MinimumVersion, GuestProtocol.CurrentVersion);

    [TestMethod]
    public void Heartbeat_RoundTripsAndRejectsUnknownSchema()
    {
        var heartbeat = GuestAgentHeartbeat.Create(
            Host(),
            GuestReadinessFailure.None,
            ExecutionTargetEpoch.Create("sandbox-1", "nonce"),
            port: 51234,
            DateTimeOffset.UtcNow);

        var parsed = GuestAgentHeartbeat.TryParse(heartbeat.ToJson());

        Assert.IsNotNull(parsed);
        Assert.AreEqual(heartbeat.BinaryHash, parsed.BinaryHash);
        Assert.AreEqual(51234, parsed.Port);
        Assert.IsTrue(parsed.Ready);

        // The heartbeat arrives through a guest-writable folder the spec treats as untrusted, so
        // parsing is total: garbage and unknown schemas produce "no usable heartbeat", never a throw.
        Assert.IsNull(GuestAgentHeartbeat.TryParse("{not json"));
        Assert.IsNull(GuestAgentHeartbeat.TryParse(string.Empty));
        Assert.IsNull(GuestAgentHeartbeat.TryParse(heartbeat.ToJson().Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Heartbeat_NotReady_StillPublishesTheReason()
    {
        var heartbeat = GuestAgentHeartbeat.Create(
            Host(),
            GuestReadinessFailure.Session0,
            ExecutionTargetEpoch.None,
            port: 0,
            DateTimeOffset.UtcNow);

        // Publishing the failure is what lets the host report why the agent refused instead of
        // timing out on silence.
        Assert.IsFalse(heartbeat.Ready);
        Assert.AreEqual(nameof(GuestReadinessFailure.Session0), heartbeat.NotReadyReason);
    }

    [TestMethod]
    public void Heartbeat_StaleTimestamp_IsNotFresh()
    {
        var now = DateTimeOffset.UtcNow;
        var heartbeat = GuestAgentHeartbeat.Create(Host(), GuestReadinessFailure.None, ExecutionTargetEpoch.None, 0, now);

        Assert.IsTrue(heartbeat.IsFresh(now));
        Assert.IsFalse(heartbeat.IsFresh(now + GuestAgentHeartbeat.MaximumAge + TimeSpan.FromSeconds(1)));
    }
}
