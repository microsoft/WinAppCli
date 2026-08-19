// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

internal sealed class WindowsSandboxTargetState
{
    public const int CurrentSchema = 1;

    public required int Schema { get; set; }

    public required string TargetId { get; set; }

    public string ProviderInstanceId { get; set; } = string.Empty;

    public string Epoch { get; set; } = string.Empty;

    public long Revision { get; set; }

    public string CreatedAtUtc { get; set; } = string.Empty;
}

internal enum WindowsSandboxStateReadStatus
{
    Missing,
    Valid,
    Corrupt,
    UnsupportedVersion,
    UnsafePath,
}

internal sealed record WindowsSandboxStateReadResult(
    WindowsSandboxStateReadStatus Status,
    WindowsSandboxTargetState? State,
    string? Error = null);

[JsonSerializable(typeof(WindowsSandboxTargetState))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal partial class WindowsSandboxStateJsonContext : JsonSerializerContext;
