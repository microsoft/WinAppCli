// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            Description = "Output directory for the loose layout package. If not specified, a directory named AppX inside the input-folder directory will be used."
        };

        ArgsOption = new Option<string>("--args")
        {
            Description = "Command-line arguments to pass to the application"
        };

        NoLaunchOption = new Option<bool>("--no-launch")
        {
            Description = "Only create the debug identity and register the package without launching the application"
        };
    }

    public RunCommand() : base("run", "Creates packaged layout, registers the Application, and launches the packaged application.")
    {
        Arguments.Add(InputFolderArgument);
        Options.Add(ManifestOption);
        Options.Add(OutputAppXDirectoryOption);
        Options.Add(ArgsOption);
        Options.Add(NoLaunchOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IMsixService msixService,
        IAppLauncherService appLauncherService,
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
            var isJson = parseResult.GetValue(WinAppRootCommand.JsonOption);

            uint processId = 0;
            string? packageFamilyName = null;
            string? publisher = null;
            string? applicationId = null;
            string? aumid = null;
            string? errorMessage = null;
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

                    outputAppXDirectory ??= new DirectoryInfo(Path.Combine(inputFolder.FullName, "AppX"));

                    // Step 2: Create and register the debug identity
                    taskContext.AddDebugMessage($"{UiSymbols.Package} Creating debug identity...");
                    var identityResult = await msixService.AddLooseLayoutIdentityAsync(
                        resolvedManifest,
                        inputFolder,
                        outputAppXDirectory,
                        taskContext,
                        cancellationToken);

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

                    if (noLaunch)
                    {
                        return (0, $"{packageFamilyName} registered (AUMID: {aumid})");
                    }

                    // Step 3: Launch the application using IApplicationActivationManager
                    taskContext.AddDebugMessage($"{UiSymbols.Rocket} Launching application...");
                    processId = appLauncherService.LaunchByAumid(aumid, appArgs);

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
                    PrintJson(aumid, processId: null, errorMessage);
                }
                return success;
            }

            if (noLaunch)
            {
                if (isJson)
                {
                    PrintJson(aumid, processId: null, errorMessage: null);
                }
                return success;
            }

            if (isJson)
            {
                PrintJson(aumid, processId, errorMessage: null);
            }

            // Wait for the launched process to exit before returning.
            // The process may have already exited by the time we get here (common for
            // fast-starting apps), in which case GetProcessById throws ArgumentException.
            // PIDs above int.MaxValue cannot be tracked via Process.GetProcessById.
            if (processId > int.MaxValue)
            {
                return 0;
            }

            try
            {
                using var process = Process.GetProcessById(unchecked((int)processId));
                await process.WaitForExitAsync(cancellationToken);
                
                return process.ExitCode;
            }
            catch (ArgumentException)
            {
                // Process already exited before we could attach — treat as success.
                return 0;
            }
        }

        void PrintJson(string? aumid, uint? processId, string? errorMessage)
        {
            var result = new RunCommandResult
            {
                AUMID = aumid,
                ProcessId = processId,
                Error = errorMessage
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
}

internal sealed class RunCommandResult
{
    public string? AUMID { get; set; }
    public uint? ProcessId { get; set; }
    public string? Error { get; set; }
}

[JsonSerializable(typeof(RunCommandResult))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class RunCommandJsonContext : JsonSerializerContext;
