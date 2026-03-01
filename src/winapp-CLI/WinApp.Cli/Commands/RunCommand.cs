// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal partial class RunCommand : Command, IShortDescription
{
    public string ShortDescription => "Create debug identity and launch the packaged application.";

    public static Argument<DirectoryInfo> InputFolderArgument { get; }
    public static Option<FileInfo> ManifestOption { get; }
    public static Option<DirectoryInfo?> OutputAppXDirectoryOption { get; }
    public static Option<string> ArgsOption { get; }
    public static Option<bool> NoLaunchOption { get; }
    public static Option<bool> DebugOutputOption { get; }

    static RunCommand()
    {
        InputFolderArgument = new Argument<DirectoryInfo>("input-folder")
        {
            Description = "Input folder containing the app to run",
            Arity = ArgumentArity.ExactlyOne
        };
        InputFolderArgument.AcceptExistingOnly();

        ManifestOption = new Option<FileInfo>("--manifest")
        {
            Description = "Path to the appxmanifest.xml (default: auto-detect from input folder or current directory)"
        };
        ManifestOption.AcceptExistingOnly();

        OutputAppXDirectoryOption = new Option<DirectoryInfo?>("--output-appx-directory")
        {
            Description = "Output directory for the loose layout package. If not specified, A directory named AppX inside the appxmanifest.xml's directory will be used."
        };

        ArgsOption = new Option<string>("--args")
        {
            Description = "Command-line arguments to pass to the application"
        };

        NoLaunchOption = new Option<bool>("--no-launch")
        {
            Description = "Only create the debug identity and register the package without launching the application"
        };

        DebugOutputOption = new Option<bool>("--debug-output")
        {
            Description = "Capture and display debug output (Debug.WriteLine, OutputDebugString) and stdout/stderr from the launched application. Note: only one debugger can attach — VS Code/Visual Studio debugger cannot attach simultaneously."
        };
    }

    public RunCommand() : base("run", "Creates packaged layout, registers the Application, and launches the packaged application.")
    {
        Arguments.Add(InputFolderArgument);
        Options.Add(ManifestOption);
        Options.Add(OutputAppXDirectoryOption);
        Options.Add(ArgsOption);
        Options.Add(NoLaunchOption);
        Options.Add(DebugOutputOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IMsixService msixService,
        IAppLauncherService appLauncherService,
        IDebugOutputService debugOutputService,
        ICurrentDirectoryProvider currentDirectoryProvider,
        IAnsiConsole ansiConsole,
        IStatusService statusService) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var inputFolder = parseResult.GetRequiredValue(InputFolderArgument);
            var manifest = parseResult.GetValue(ManifestOption);
            var outputAppXDirectory = parseResult.GetValue(OutputAppXDirectoryOption);
            var appArgs = parseResult.GetValue(ArgsOption);
            var noLaunch = parseResult.GetValue(NoLaunchOption);
            var debugOutput = parseResult.GetValue(DebugOutputOption);
            var isJson = parseResult.GetValue(WinAppRootCommand.JsonOption);

            uint processId = 0;
            Process? aliasProcess = null;
            string? packageFamilyName = null;
            string? publisher = null;
            string? applicationId = null;
            string? packageName = null;
            string? aumid = null;
            string? aliasName = null;
            string? errorMessage = null;
            DirectoryInfo? resolvedOutputDir = null;
            var statusMessage = noLaunch ? "Registering packaged application..." : "Launching packaged application...";
            var success = await statusService.ExecuteWithStatusAsync(statusMessage, async (taskContext, cancellationToken) =>
            {
                try
                {
                    // Resolve manifest with priority: --manifest → input folder → cwd
                    FileInfo resolvedManifest;
                    if (manifest != null)
                    {
                        resolvedManifest = manifest;
                        taskContext.AddDebugMessage($"{UiSymbols.Note} Using specified manifest: {resolvedManifest}");
                    }
                    else
                    {
                        var folderManifest = FindManifest(inputFolder.FullName);
                        if (folderManifest.Exists)
                        {
                            resolvedManifest = folderManifest;
                            taskContext.AddDebugMessage($"{UiSymbols.Note} Using manifest from input folder: {resolvedManifest}");
                        }
                        else
                        {
                            var cwdManifest = FindManifest(currentDirectoryProvider.GetCurrentDirectory());
                            if (cwdManifest.Exists)
                            {
                                resolvedManifest = cwdManifest;
                                taskContext.AddDebugMessage($"{UiSymbols.Note} Using manifest from current directory: {resolvedManifest}");
                            }
                            else
                            {
                                throw new FileNotFoundException(
                                    $"Manifest file not found. Searched in: input folder ({inputFolder.FullName}), current directory ({currentDirectoryProvider.GetCurrentDirectory()}). Use --manifest to specify the path.");
                            }
                        }
                    }

                    resolvedOutputDir = outputAppXDirectory ?? new DirectoryInfo(Path.Combine(inputFolder.FullName, "AppX"));
                    outputAppXDirectory = resolvedOutputDir;

                    // Step 2: Create and register the debug identity
                    taskContext.AddDebugMessage($"{UiSymbols.Package} Creating debug identity...");

                    // If --debug-output, prepare a manifest transform to inject execution alias before registration
                    Func<string, DirectoryInfo, string>? manifestTransform = null;
                    if (debugOutput && !noLaunch)
                    {
                        manifestTransform = (content, outDir) =>
                        {
                            // Find the executable name from the manifest
                            var exeMatch = ApplicationExecutableRegex().Match(content);
                            var executableName = exeMatch.Success ? exeMatch.Groups[1].Value : "app.exe";

                            // Extract package name from the manifest Identity element
                            var identity = MsixService.ParseAppxManifestAsync(content);

                            // Generate alias and inject into manifest
                            aliasName = ManifestAliasHelper.GenerateAliasName(identity.PackageName);
                            content = ManifestAliasHelper.InjectExecutionAlias(content, aliasName, executableName);

                            taskContext.AddDebugMessage($"{UiSymbols.Note} Injected execution alias '{aliasName}'");
                            return content;
                        };
                    }

                    var identityResult = await msixService.AddLooseLayoutIdentityAsync(
                        resolvedManifest,
                        inputFolder,
                        outputAppXDirectory,
                        taskContext,
                        manifestTransform,
                        cancellationToken);

                    packageName = identityResult.PackageName;
                    packageFamilyName = appLauncherService.ComputePackageFamilyName(
                        identityResult.PackageName,
                        identityResult.Publisher);
                    publisher = identityResult.Publisher;
                    applicationId = identityResult.ApplicationId;
                    aumid = $"{packageFamilyName}!{applicationId}";

                    taskContext.AddDebugMessage($"{UiSymbols.Package} Package: {identityResult.PackageName}");
                    taskContext.AddDebugMessage($"{UiSymbols.User} Publisher: {publisher}");
                    taskContext.AddDebugMessage($"{UiSymbols.Id} App ID: {applicationId}");
                    taskContext.AddDebugMessage($"{UiSymbols.Link} AUMID: {aumid}");

                    if (debugOutput && !noLaunch && aliasName != null)
                    {
                        taskContext.AddDebugMessage($"{UiSymbols.Check} Execution alias: {aliasName}");
                    }

                    if (noLaunch)
                    {
                        return (0, $"{packageFamilyName} registered (AUMID: {aumid})");
                    }

                    if (debugOutput && aliasName != null)
                    {
                        // Launch via execution alias for stdio capture
                        taskContext.AddDebugMessage($"{UiSymbols.Rocket} Launching application via execution alias...");
                        aliasProcess = appLauncherService.LaunchByAlias(aliasName, appArgs);
                        processId = (uint)aliasProcess.Id;
                        taskContext.AddDebugMessage($"{UiSymbols.Note} Debug output enabled. Only one debugger can attach — VS Code/Visual Studio debugger cannot attach simultaneously.");
                    }
                    else
                    {
                        // Standard COM activation launch
                        taskContext.AddDebugMessage($"{UiSymbols.Rocket} Launching application...");
                        processId = appLauncherService.LaunchByAumid(aumid, appArgs);
                    }

                    return (0, $"{packageFamilyName} launched (PID: {processId})");
                }
                catch (Exception error)
                {
                    errorMessage = error.Message;
                    return (1, $"{UiSymbols.Error} Failed to launch application: {error.Message}");
                }
            }, cancellationToken);

            if (success != 0)
            {
                if (isJson)
                {
                    PrintJson(aumid, processId: null, errorMessage, debugOutput);
                }
                return success;
            }

            if (noLaunch)
            {
                if (isJson)
                {
                    PrintJson(aumid, processId: null, errorMessage: null, debugOutput);
                }
                return success;
            }

            if (isJson)
            {
                PrintJson(aumid, processId, errorMessage: null, debugOutput);
            }

            if (debugOutput)
            {
                return await RunWithDebugOutputAsync(processId, aliasProcess, cancellationToken);
            }

            // Standard flow: wait for the launched process to exit
            using var proc = Process.GetProcessById((int)processId);
            await proc.WaitForExitAsync(cancellationToken);
            return proc.ExitCode;
        }

        /// <summary>
        /// Runs the launched app while capturing debug output (OutputDebugString) and stdio.
        /// </summary>
        private async Task<int> RunWithDebugOutputAsync(uint processId, Process? aliasProcess, CancellationToken cancellationToken)
        {
            var output = Console.Error;

            // Start Win32 Debug API capture on a dedicated thread
            var debugTask = debugOutputService.RunDebugEventLoopAsync(processId, output, cancellationToken);

            // If launched via alias, pipe stdout/stderr from the original Process object
            Task? stdoutTask = null;
            Task? stderrTask = null;

            if (aliasProcess != null)
            {
                stdoutTask = PipeStreamAsync(aliasProcess.StandardOutput, output, "[STDOUT]", cancellationToken);
                stderrTask = PipeStreamAsync(aliasProcess.StandardError, output, "[STDERR]", cancellationToken);
            }

            // Wait for the debug event loop to complete (EXIT_PROCESS_DEBUG_EVENT)
            await debugTask;

            // Give stdio streams a short time to flush after process exit
            if (stdoutTask != null || stderrTask != null)
            {
                var streamTasks = new List<Task>();
                if (stdoutTask != null)
                {
                    streamTasks.Add(stdoutTask);
                }
                if (stderrTask != null)
                {
                    streamTasks.Add(stderrTask);
                }
                await Task.WhenAny(Task.WhenAll(streamTasks), Task.Delay(2000, cancellationToken));
            }

            try
            {
                if (aliasProcess != null)
                {
                    aliasProcess.WaitForExit(0);
                    return aliasProcess.ExitCode;
                }
                using var proc = Process.GetProcessById((int)processId);
                return proc.ExitCode;
            }
            catch
            {
                return 0;
            }
        }

        private static async Task PipeStreamAsync(StreamReader reader, TextWriter output, string prefix, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line == null)
                    {
                        break;
                    }
                    await output.WriteLineAsync($"{prefix} {line}");
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
        }

        void PrintJson(string? aumid, uint? processId, string? errorMessage, bool debugOutput)
        {
            var result = new RunCommandResult
            {
                AUMID = aumid,
                ProcessId = processId,
                Error = errorMessage,
                DebugOutput = debugOutput ? true : null
            };

            var json = JsonSerializer.Serialize(result, RunCommandJsonContext.Default.RunCommandResult);
            ansiConsole.WriteLine(json);
        }

        private static FileInfo FindManifest(string directory)
        {
            var manifestPath = Path.Combine(directory, "appxmanifest.xml");
            if (File.Exists(manifestPath))
            {
                return new FileInfo(manifestPath);
            }
            manifestPath = Path.Combine(directory, "Package.appxmanifest");
            return new FileInfo(manifestPath);
        }
    }

    [GeneratedRegex(@"<Application[^>]*Executable\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex ApplicationExecutableRegex();
}

internal sealed class RunCommandResult
{
    public string? AUMID { get; set; }
    public uint? ProcessId { get; set; }
    public string? Error { get; set; }
    public bool? DebugOutput { get; set; }
}

[JsonSerializable(typeof(RunCommandResult))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class RunCommandJsonContext : JsonSerializerContext;
