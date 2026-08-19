// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

internal sealed class WindowsSandboxStateStore(
    IExecutionTargetStateDirectoryProvider directoryProvider) : IWindowsSandboxStateStore
{
    private const string StateFileName = "target-state.json";

    public FileInfo GetStateFile() =>
        new(Path.Combine(
            directoryProvider.GetTargetDirectory(ExecutionTargetRef.WindowsSandboxDefault).FullName,
            StateFileName));

    public async Task<WindowsSandboxStateReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var stateFile = GetStateFile();
        if (IsUnsafe(stateFile))
        {
            return new WindowsSandboxStateReadResult(
                WindowsSandboxStateReadStatus.UnsafePath,
                null,
                $"Windows Sandbox target state path '{stateFile.FullName}' is unsafe.");
        }

        if (!stateFile.Exists)
        {
            return new WindowsSandboxStateReadResult(WindowsSandboxStateReadStatus.Missing, null);
        }

        try
        {
            await using var stream = File.OpenRead(stateFile.FullName);
            var state = await JsonSerializer.DeserializeAsync(
                stream,
                WindowsSandboxStateJsonContext.Default.WindowsSandboxTargetState,
                cancellationToken);

            if (state is null)
            {
                return Corrupt("Windows Sandbox target state deserialized to null.");
            }

            if (state.Schema != WindowsSandboxTargetState.CurrentSchema)
            {
                return new WindowsSandboxStateReadResult(
                    WindowsSandboxStateReadStatus.UnsupportedVersion,
                    null,
                    $"Windows Sandbox target state schema {state.Schema} is not supported by this version of winapp.");
            }

            if (!IsValid(state, out var error))
            {
                return Corrupt(error);
            }

            return new WindowsSandboxStateReadResult(WindowsSandboxStateReadStatus.Valid, state);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Corrupt($"Failed to read Windows Sandbox target state: {ex.Message}");
        }
    }

    public async Task WriteAsync(
        WindowsSandboxTargetState state,
        CancellationToken cancellationToken = default)
    {
        if (!IsValid(state, out var error))
        {
            throw new ArgumentException(error, nameof(state));
        }

        var stateFile = GetStateFile();
        if (IsUnsafe(stateFile))
        {
            throw new IOException($"Windows Sandbox target state path '{stateFile.FullName}' is unsafe.");
        }

        var targetDirectory = directoryProvider.GetTargetDirectory(ExecutionTargetRef.WindowsSandboxDefault);
        targetDirectory.Create();
        if (IsUnsafe(stateFile))
        {
            throw new IOException($"Windows Sandbox target state path '{stateFile.FullName}' is unsafe.");
        }

        var json = JsonSerializer.Serialize(
            state,
            WindowsSandboxStateJsonContext.Default.WindowsSandboxTargetState);
        await PathSafety.AtomicWriteAllTextAsync(
            stateFile.FullName,
            json + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    private bool IsUnsafe(FileInfo stateFile) =>
        PathSafety.HasReparsePointOnPath(
            stateFile.FullName,
            directoryProvider.GetStateRoot().FullName);

    private static bool IsValid(WindowsSandboxTargetState state, out string error)
    {
        if (state.Schema != WindowsSandboxTargetState.CurrentSchema)
        {
            error = $"Windows Sandbox target state schema must be {WindowsSandboxTargetState.CurrentSchema}.";
            return false;
        }

        if (!string.Equals(
                state.TargetId,
                ExecutionTargetRef.WindowsSandboxDefaultId,
                StringComparison.Ordinal))
        {
            error = $"Windows Sandbox target state must target '{ExecutionTargetRef.WindowsSandboxDefaultId}'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(state.ProviderInstanceId))
        {
            error = "Windows Sandbox target state must contain a provider instance ID.";
            return false;
        }

        try
        {
            _ = new ExecutionTargetEpoch(state.Epoch);
        }
        catch (ArgumentException)
        {
            error = "Windows Sandbox target state contains an invalid epoch.";
            return false;
        }

        if (state.Revision <= 0)
        {
            error = "Windows Sandbox target state revision is outside the supported range.";
            return false;
        }

        if (!DateTimeOffset.TryParse(
                state.CreatedAtUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _))
        {
            error = "Windows Sandbox target state contains an invalid creation timestamp.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static WindowsSandboxStateReadResult Corrupt(string error) =>
        new(WindowsSandboxStateReadStatus.Corrupt, null, error);
}
