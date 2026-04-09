// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Text;

namespace WinApp.Cli.Services;

/// <summary>
/// Manages the named pipe server for receiving managed exception data from the
/// startup hook DLL injected into the target process.
/// </summary>
internal sealed class CrashHookService(ILogger<CrashHookService> logger) : ICrashHookService, IDisposable
{
    private NamedPipeServerStream? _pipeServer;
    private Task? _readerTask;
    private string? _pipeName;
    private readonly ConcurrentQueue<CrashHookException> _exceptions = new();
    private const int MaxStoredExceptions = 50;

    private readonly CancellationTokenSource _cts = new();

    /// <inheritdoc/>
    public IReadOnlyList<CrashHookException> CapturedExceptions
    {
        get
        {
            var list = _exceptions.ToArray();
            Array.Reverse(list); // Most recent first
            return list;
        }
    }

    /// <inheritdoc/>
    public string? Setup(string appxDirectory)
    {
        try
        {
            // Extract the embedded crash hook DLL to the AppX directory
            var hookDllPath = Path.Combine(appxDirectory, "winapp-crash-hook.dll");
            ExtractHookDll(hookDllPath);

            // Copy PDB files from the input directory to the AppX directory so that
            // StackTrace can resolve source file:line info. PDBs live next to the
            // build output but the app runs from AppX.
            CopyPdbFiles(appxDirectory);

            // Create the named pipe server with a unique name
            _pipeName = $"winapp-crash-{Environment.ProcessId}";
            _pipeServer = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.In,
                1, // single client
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            // Inject the startup hook into the app's runtimeconfig.json.
            // For AUMID-launched packaged apps, env var propagation via EnableDebugging
            // is unreliable, so we inject directly into configProperties instead.
            InjectRuntimeConfig(appxDirectory, hookDllPath, _pipeName);

            // Also return env string for --with-alias path (ProcessStartInfo.Environment)
            var envBuilder = new StringBuilder();
            envBuilder.Append($"DOTNET_STARTUP_HOOKS={hookDllPath}");
            envBuilder.Append('\0');
            envBuilder.Append($"WINAPP_CRASH_PIPE={_pipeName}");
            envBuilder.Append('\0');
            envBuilder.Append('\0');

            logger.LogDebug("CrashHook: Pipe={PipeName}, Hook={HookPath}", _pipeName, hookDllPath);
            return envBuilder.ToString();
        }
        catch (Exception ex)
        {
            logger.LogDebug("CrashHook setup failed: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Injects STARTUP_HOOKS and the pipe name into the app's runtimeconfig.json.
    /// This works for AUMID-launched packaged apps where env var propagation fails.
    /// The CLR reads configProperties from runtimeconfig.json on startup.
    /// </summary>
    private void InjectRuntimeConfig(string appxDirectory, string hookDllPath, string pipeName)
    {
        // Find the runtimeconfig.json — typically <appname>.runtimeconfig.json
        var runtimeConfigs = Directory.GetFiles(appxDirectory, "*.runtimeconfig.json");
        if (runtimeConfigs.Length == 0)
        {
            logger.LogDebug("CrashHook: No runtimeconfig.json found in {Dir}", appxDirectory);
            return;
        }

        foreach (var configPath in runtimeConfigs)
        {
            try
            {
                var json = File.ReadAllText(configPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);

                // Rebuild with injected properties
                using var ms = new MemoryStream();
                using (var writer = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Name == "runtimeOptions")
                        {
                            writer.WritePropertyName("runtimeOptions");
                            writer.WriteStartObject();
                            foreach (var roProp in prop.Value.EnumerateObject())
                            {
                                if (roProp.Name == "configProperties")
                                {
                                    writer.WritePropertyName("configProperties");
                                    writer.WriteStartObject();
                                    foreach (var cpProp in roProp.Value.EnumerateObject())
                                    {
                                        cpProp.WriteTo(writer);
                                    }
                                    // Inject our hooks
                                    writer.WriteString("STARTUP_HOOKS", hookDllPath);
                                    writer.WriteString("WINAPP_CRASH_PIPE", pipeName);
                                    writer.WriteEndObject();
                                }
                                else
                                {
                                    roProp.WriteTo(writer);
                                }
                            }
                            // If configProperties didn't exist, add it
                            if (!prop.Value.TryGetProperty("configProperties", out _))
                            {
                                writer.WritePropertyName("configProperties");
                                writer.WriteStartObject();
                                writer.WriteString("STARTUP_HOOKS", hookDllPath);
                                writer.WriteString("WINAPP_CRASH_PIPE", pipeName);
                                writer.WriteEndObject();
                            }
                            writer.WriteEndObject();
                        }
                        else
                        {
                            prop.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }

                File.WriteAllBytes(configPath, ms.ToArray());
                logger.LogDebug("CrashHook: Injected into {Config}", configPath);
            }
            catch (Exception ex)
            {
                logger.LogDebug("CrashHook: Failed to inject into {Config}: {Message}", configPath, ex.Message);
            }
        }
    }

    /// <inheritdoc/>
    public void StartReading()
    {
        if (_pipeServer == null)
        {
            return;
        }

        _readerTask = Task.Run(() => ReadFromPipe());
    }

    /// <inheritdoc/>
    public async Task WaitForCompletionAsync(TimeSpan timeout)
    {
        if (_readerTask == null)
        {
            return;
        }

        try
        {
            await _readerTask.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            logger.LogDebug("CrashHook reader timed out after {Timeout}.", timeout);
        }
        catch
        {
            // Task faulted or cancelled
        }
        finally
        {
            _pipeServer?.Dispose();
        }
    }

    private void ReadFromPipe()
    {
        try
        {
            if (_pipeServer == null)
            {
                return;
            }

            // Wait for the startup hook in the target process to connect
            _pipeServer.WaitForConnection();
            logger.LogDebug("CrashHook: Target process connected to pipe.");

            using var reader = new StreamReader(_pipeServer, Encoding.UTF8);

            string? exType = null, exMessage = null, exHResult = null;
            var stackBuilder = new StringBuilder();
            bool inStack = false;

            while (true)
            {
                var line = reader.ReadLine();
                if (line == null)
                {
                    break; // Pipe closed (process exited)
                }

                if (line == "---END---")
                {
                    // Complete exception block — store it
                    if (exType != null)
                    {
                        var stackTrace = stackBuilder.ToString().TrimEnd();
                        _exceptions.Enqueue(new CrashHookException(
                            exType,
                            exMessage ?? "",
                            exHResult ?? "",
                            stackTrace));

                        while (_exceptions.Count > MaxStoredExceptions)
                        {
                            _exceptions.TryDequeue(out _);
                        }

                        logger.LogDebug("CrashHook: {Type}: {Message}", exType, exMessage);
                    }

                    // Reset for next exception
                    exType = null;
                    exMessage = null;
                    exHResult = null;
                    stackBuilder.Clear();
                    inStack = false;
                }
                else if (inStack)
                {
                    stackBuilder.AppendLine(line);
                }
                else if (line.StartsWith("Type: "))
                {
                    exType = line[6..];
                }
                else if (line.StartsWith("Message: "))
                {
                    exMessage = line[9..];
                }
                else if (line.StartsWith("HResult: "))
                {
                    exHResult = line[9..];
                }
                else if (line == "Stack:")
                {
                    inStack = true;
                }
            }
        }
        catch (IOException)
        {
            // Pipe broken — target process exited
            logger.LogDebug("CrashHook: Pipe closed (process exited).");
        }
        catch (Exception ex)
        {
            logger.LogDebug("CrashHook reader failed: {Message}", ex.Message);
        }
    }

    private static void ExtractHookDll(string targetPath)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("WinApp.Cli.Assets.winapp-crash-hook.dll")
            ?? throw new InvalidOperationException("Embedded winapp-crash-hook.dll not found.");

        using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.CopyTo(fileStream);
    }

    /// <summary>
    /// Copies PDB files from the parent of the AppX directory (the build output)
    /// into the AppX directory so StackTrace can resolve file:line info.
    /// </summary>
    private static void CopyPdbFiles(string appxDirectory)
    {
        // The AppX directory is typically <input-folder>/AppX.
        // PDBs are in <input-folder> alongside the DLLs/EXEs.
        var parentDir = Path.GetDirectoryName(appxDirectory);
        if (parentDir == null || !Directory.Exists(parentDir))
        {
            return;
        }

        foreach (var pdbFile in Directory.GetFiles(parentDir, "*.pdb", SearchOption.TopDirectoryOnly))
        {
            var destPath = Path.Combine(appxDirectory, Path.GetFileName(pdbFile));
            try
            {
                File.Copy(pdbFile, destPath, overwrite: true);
            }
            catch
            {
                // PDB may be locked — skip silently
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _pipeServer?.Dispose();
        _cts.Dispose();
    }
}
