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
    public static Option<bool> WithAliasOption { get; }
    public static Option<bool> DebugOutputOption { get; }
    public static Option<bool> UnregisterOnExitOption { get; }
    public static Option<DirectoryInfo?> CrashDumpPathOption { get; }
    public static Option<bool> DetachOption { get; }
    public static Option<bool> CleanOption { get; }

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

        WithAliasOption = new Option<bool>("--with-alias")
        {
            Description = "Launch the app using its execution alias instead of AUMID activation. The app runs in the current terminal with inherited stdin/stdout/stderr. Requires a uap5:ExecutionAlias in the manifest. Use \"winapp manifest add-alias\" to add an execution alias to the manifest."
        };

        DebugOutputOption = new Option<bool>("--debug-output")
        {
            Description = "Capture OutputDebugString messages and first-chance exceptions from the launched application. Only one debugger can attach to a process at a time, so other debuggers (Visual Studio, VS Code) cannot be used simultaneously. Use --no-launch instead if you need to attach a different debugger. Cannot be combined with --no-launch or --json."
        };

        UnregisterOnExitOption = new Option<bool>("--unregister-on-exit")
        {
            Description = "Unregister the development package after the application exits. Only removes packages registered in development mode."
        };

        CrashDumpPathOption = new Option<DirectoryInfo?>("--crash-dump-path")
        {
            Description = "Directory to save crash dump files when used with --debug-output. If the app crashes, a mini-dump is automatically written to this directory."
        };

        DetachOption = new Option<bool>("--detach")
        {
            Description = "Launch the application and return immediately without waiting for it to exit. Useful for CI/automation where you need to interact with the app after launch. Prints the PID to stdout (or in JSON with --json)."
        };
        
        CleanOption = new Option<bool>("--clean")
        {
            Description = "Remove the existing package's application data (LocalState, settings, etc.) before re-deploying. By default, application data is preserved across re-deployments."
        };
    }

    public RunCommand() : base("run", "Creates packaged layout, registers the Application, and launches the packaged application.")
    {
        Arguments.Add(InputFolderArgument);
        Options.Add(ManifestOption);
        Options.Add(OutputAppXDirectoryOption);
        Options.Add(ArgsOption);
        Options.Add(NoLaunchOption);
        Options.Add(WithAliasOption);
        Options.Add(DebugOutputOption);
        Options.Add(UnregisterOnExitOption);
        Options.Add(CrashDumpPathOption);
        Options.Add(DetachOption);
        Options.Add(CleanOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public partial class Handler(
        IMsixService msixService,
        IAppLauncherService appLauncherService,
        IPackageRegistrationService packageRegistrationService,
        IDebugOutputService debugOutputService,
        ICrashHookService crashHookService,
        ICrashReportService crashReportService,
        ICurrentDirectoryProvider currentDirectoryProvider,
        IAnsiConsole ansiConsole,
        IStatusService statusService,
        ILogger<RunCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var inputFolder = parseResult.GetRequiredValue(InputFolderArgument);
            var manifest = parseResult.GetValue(ManifestOption);
            var outputAppXDirectory = parseResult.GetValue(OutputAppXDirectoryOption);
            var appArgs = parseResult.GetValue(ArgsOption);
            var noLaunch = parseResult.GetValue(NoLaunchOption);
            var withAlias = parseResult.GetValue(WithAliasOption);
            var debugOutput = parseResult.GetValue(DebugOutputOption);
            var unregisterOnExit = parseResult.GetValue(UnregisterOnExitOption);
            var crashDumpPath = parseResult.GetValue(CrashDumpPathOption);
            var detach = parseResult.GetValue(DetachOption);
            var clean = parseResult.GetValue(CleanOption);
            var isJson = parseResult.GetValue(WinAppRootCommand.JsonOption);

            // Validate mutually exclusive options
            if (withAlias && noLaunch)
            {
                logger.LogError("{UISymbol} --with-alias and --no-launch cannot be used together.", UiSymbols.Error);
                return 1;
            }

            if (debugOutput && noLaunch)
            {
                logger.LogError("{UISymbol} --debug-output and --no-launch cannot be used together.", UiSymbols.Error);
                return 1;
            }

            if (isJson && debugOutput)
            {
                logger.LogError("{UISymbol} --json and --debug-output cannot be used together.", UiSymbols.Error);
                return 1;
            }

            if (isJson && withAlias)
            {
                logger.LogError("{UISymbol} --json and --with-alias cannot be used together.", UiSymbols.Error);
                return 1;
            }

            if (unregisterOnExit && noLaunch)
            {
                logger.LogError("{UISymbol} --unregister-on-exit and --no-launch cannot be used together.", UiSymbols.Error);
                return 1;
            }

            if (detach && noLaunch)
            {
                logger.LogError("{UISymbol} --detach and --no-launch cannot be used together.", UiSymbols.Error);
                return 1;
            }

            if (detach && debugOutput)
            {
                logger.LogError("{UISymbol} --detach and --debug-output cannot be used together.", UiSymbols.Error);
                return 1;
            }

            if (detach && withAlias)
            {
                logger.LogError("{UISymbol} --detach and --with-alias cannot be used together.", UiSymbols.Error);
                return 1;
            }

            if (detach && unregisterOnExit)
            {
                logger.LogError("{UISymbol} --detach and --unregister-on-exit cannot be used together.", UiSymbols.Error);
                return 1;
            }

            uint processId = 0;
            string? packageFamilyName = null;
            string? packageFullName = null;
            string? packageName = null;
            string? publisher = null;
            string? applicationId = null;
            string? aumid = null;
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

                    outputAppXDirectory ??= new DirectoryInfo(Path.Combine(inputFolder.FullName, "AppX"));
                    resolvedOutputDir = outputAppXDirectory;

                    // Step 2: Create and register the debug identity
                    taskContext.AddDebugMessage($"{UiSymbols.Package} Creating debug identity...");
                    var identityResult = await msixService.AddLooseLayoutIdentityAsync(
                        resolvedManifest,
                        inputFolder,
                        outputAppXDirectory,
                        taskContext,
                        clean,
                        cancellationToken);

                    packageFamilyName = appLauncherService.ComputePackageFamilyName(
                        identityResult.PackageName,
                        identityResult.Publisher);
                    packageFullName = appLauncherService.GetPackageFullName(packageFamilyName);
                    packageName = identityResult.PackageName;
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

                    if (withAlias)
                    {
                        // --with-alias: skip AUMID launch, will launch via execution alias after status completes
                        taskContext.AddDebugMessage($"{UiSymbols.Rocket} Will launch via execution alias...");
                        return (0, $"{packageFamilyName} registered (AUMID: {aumid})");
                    }

                    // Set up crash hook for managed exception capture when using --debug-output.
                    // This injects a tiny DLL via DOTNET_STARTUP_HOOKS that captures exception
                    // type, message, and full managed stack trace over a named pipe.
                    string? crashHookEnv = null;
                    if (debugOutput && resolvedOutputDir != null)
                    {
                        crashHookEnv = crashHookService.Setup(resolvedOutputDir.FullName);
                        if (crashHookEnv != null && packageFullName != null)
                        {
                            try
                            {
                                var debugSettings = Windows.Win32.UI.Shell.PackageDebugSettings.CreateInstance<Windows.Win32.UI.Shell.IPackageDebugSettings>();

                                // Build PZZWSTR as a char array with explicit null terminators.
                                // PZZWSTR format: "KEY=VALUE\0KEY=VALUE\0\0"
                                var envChars = crashHookEnv.ToCharArray();

                                unsafe
                                {
                                    fixed (char* pEnv = envChars)
                                    fixed (char* pPkg = packageFullName)
                                    {
                                        debugSettings.EnableDebugging(pPkg, null,
                                            new Windows.Win32.Foundation.PZZWSTR(pEnv));
                                    }
                                }
                                taskContext.AddDebugMessage($"{UiSymbols.Check} Crash hook: EnableDebugging for {packageFullName}");
                            }
                            catch (Exception ex)
                            {
                                taskContext.AddDebugMessage($"{UiSymbols.Warning} Crash hook EnableDebugging failed: {ex.GetType().Name}: {ex.Message}");
                                crashHookEnv = null;
                            }
                        }
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

            // --detach: return immediately after launch without waiting for exit
            if (detach)
            {
                if (isJson)
                {
                    PrintJson(aumid, processId, errorMessage: null);
                }
                return 0;
            }

            // --with-alias: launch via execution alias with inherited stdio
            if (withAlias)
            {
                var dumpDir = debugOutput ? crashDumpPath?.FullName : null;
                var aliasExitCode = await LaunchViaExecutionAliasAsync(resolvedOutputDir!, appArgs, debugOutput, dumpDir, packageFullName, cancellationToken);
                crashReportService.PrintExitCodeSummary(aliasExitCode);
                if (unregisterOnExit && packageName != null)
                {
                    await UnregisterDevPackageAsync(packageName, cancellationToken);
                }
                return aliasExitCode;
            }

            if (isJson)
            {
                PrintJson(aumid, processId, errorMessage: null);
            }

            // Record time before waiting/debugging for Event Log queries after crash
            var crashTime = DateTime.UtcNow;

            // --debug-output: run the debug event loop.
            // The crash hook DLL (if injected) captures managed exceptions via
            // FirstChanceException and writes them to our named pipe synchronously
            // on the exception's own thread — no EventPipe buffering issues.
            if (debugOutput)
            {
                // Start reading from the crash hook pipe (background thread)
                crashHookService.StartReading();

                // Only write crash dumps when --crash-dump-path is explicitly provided
                var dumpDir = crashDumpPath?.FullName;
                var exitCode = await debugOutputService.RunDebugLoopAsync(processId, dumpDir, cancellationToken);

                // Wait for the crash hook pipe to drain (process exited → pipe closed)
                await crashHookService.WaitForCompletionAsync(TimeSpan.FromSeconds(3));

                // After crash exit, print managed exception details.
                // Prefer crash hook (has full stack with file:line), only fall back to
                // Event Log when the crash hook didn't capture anything (e.g., FailFast,
                // StackOverflow, or non-.NET apps that bypass FirstChanceException).
                if (exitCode != 0 && !cancellationToken.IsCancellationRequested)
                {
                    await crashReportService.PrintCrashReportAsync(crashHookService, processId, crashTime);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    appLauncherService.TerminatePackageProcesses(packageFullName, processId);
                }

                // Clean up EnableDebugging state
                if (packageFullName != null)
                {
                    try
                    {
                        var debugSettings = Windows.Win32.UI.Shell.PackageDebugSettings.CreateInstance<Windows.Win32.UI.Shell.IPackageDebugSettings>();
                        unsafe
                        {
                            fixed (char* pPkg = packageFullName)
                            {
                                debugSettings.DisableDebugging(pPkg);
                            }
                        }
                    }
                    catch { /* ignore cleanup errors */ }
                }

                if (unregisterOnExit && packageName != null)
                {
                    await UnregisterDevPackageAsync(packageName, cancellationToken);
                }
                return exitCode;
            }

            // Wait for the launched process to exit before returning.
            // The process may have already exited by the time we get here (common for
            // fast-starting apps), in which case GetProcessById throws ArgumentException.
            // PIDs above int.MaxValue cannot be tracked via Process.GetProcessById.
            int appExitCode;
            if (processId > int.MaxValue)
            {
                appExitCode = 0;
            }
            else
            {
                try
                {
                    using var process = Process.GetProcessById(unchecked((int)processId));
                    await process.WaitForExitAsync(cancellationToken);
                    appExitCode = process.ExitCode;
                }
                catch (ArgumentException)
                {
                    // Process already exited before we could attach — treat as success.
                    appExitCode = 0;
                }
                catch (OperationCanceledException)
                {
                    // Ctrl+C — terminate all processes belonging to the package before exiting.
                    appLauncherService.TerminatePackageProcesses(packageFullName, processId);
                    appExitCode = -1;
                }
            }

            crashReportService.PrintExitCodeSummary(appExitCode);

            // On crash, print exception details from Event Log
            if (appExitCode != 0 && !cancellationToken.IsCancellationRequested)
            {
                await crashReportService.PrintCrashReportAsync(crashHookService, processId, crashTime);
            }

            if (unregisterOnExit && packageName != null)
            {
                await UnregisterDevPackageAsync(packageName, cancellationToken);
            }

            return appExitCode;
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

        private static FileInfo FindManifest(string directory) => ManifestHelper.FindManifest(directory);

        /// <summary>
        /// Unregisters dev-mode packages matching the given name.
        /// Only removes packages where <c>IsDevelopmentMode == true</c>.
        /// </summary>
        private async Task UnregisterDevPackageAsync(string packageName, CancellationToken cancellationToken)
        {
            try
            {
                var packages = packageRegistrationService.FindDevPackages(packageName);
                foreach (var pkg in packages)
                {
                    if (!pkg.IsDevelopmentMode)
                    {
                        continue;
                    }

                    await packageRegistrationService.UnregisterAsync(pkg.Name, preserveAppData: false, cancellationToken);
                    logger.LogDebug("Unregistered package {FullName} on exit.", pkg.FullName);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug("Failed to unregister package on exit: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Launches the app using its execution alias (from the processed manifest in the AppX directory).
        /// The alias process inherits stdin/stdout/stderr so console apps run inline.
        /// </summary>
        private async Task<int> LaunchViaExecutionAliasAsync(
            DirectoryInfo outputAppXDirectory,
            string? appArgs,
            bool debugOutput,
            string? crashDumpDirectory,
            string? packageFullName,
            CancellationToken cancellationToken)
        {
            // Read the processed manifest from the AppX output directory (placeholders already resolved)
            var processedManifest = new FileInfo(Path.Combine(outputAppXDirectory.FullName, "appxmanifest.xml"));
            if (!processedManifest.Exists)
            {
                logger.LogError("{UISymbol} Processed manifest not found at {Path}. Cannot determine execution alias.", UiSymbols.Error, processedManifest.FullName);
                return 1;
            }

            var content = await File.ReadAllTextAsync(processedManifest.FullName, Encoding.UTF8, cancellationToken);
            var aliases = MsixService.ExtractExecutionAliases(content);

            if (aliases.Count == 0)
            {
                logger.LogError("{UISymbol} No execution alias found in the manifest. Add one with 'winapp manifest add-alias' or use AUMID launch (without --with-alias).", UiSymbols.Error);
                return 1;
            }

            var alias = aliases[0]; // Use the first alias

            // Set up crash hook for managed exception capture
            var hookEnv = crashHookService.Setup(outputAppXDirectory.FullName);

            // Launch the execution alias process with inherited stdio
            var psi = new ProcessStartInfo
            {
                FileName = alias,
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };

            // Inject crash hook env vars via ProcessStartInfo (propagates for --with-alias)
            if (hookEnv != null)
            {
                // Parse the double-null-terminated string into individual KEY=VALUE pairs
                foreach (var entry in hookEnv.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eqIndex = entry.IndexOf('=');
                    if (eqIndex > 0)
                    {
                        psi.Environment[entry[..eqIndex]] = entry[(eqIndex + 1)..];
                    }
                }
            }

            // When not using --debug-output, configure the .NET runtime to auto-create
            // crash dumps. This is the only way to get dumps for --with-alias without
            // attaching a debugger. The dump path defaults to %TEMP%.
            if (!debugOutput)
            {
                var dumpDir = crashDumpDirectory ?? Path.GetTempPath();
                var dumpName = Path.Combine(dumpDir, "crash-%p-%t.dmp");
                psi.Environment["DOTNET_DbgEnableMiniDump"] = "1";
                psi.Environment["DOTNET_DbgMiniDumpType"] = "1"; // Mini dump
                psi.Environment["DOTNET_DbgMiniDumpName"] = dumpName;
            }

            if (!string.IsNullOrEmpty(appArgs))
            {
                psi.Arguments = appArgs;
            }

            try
            {
                using var process = Process.Start(psi);
                if (process == null)
                {
                    logger.LogError("{UISymbol} Failed to start process via execution alias '{Alias}'.", UiSymbols.Error, alias);
                    return 1;
                }

                if (debugOutput)
                {
                    // Start reading crash hook pipe
                    crashHookService.StartReading();

                    var crashTime = DateTime.UtcNow;
                    var exitCode = await debugOutputService.RunDebugLoopAsync(unchecked((uint)process.Id), crashDumpDirectory, cancellationToken);

                    // Wait for crash hook pipe to drain
                    await crashHookService.WaitForCompletionAsync(TimeSpan.FromSeconds(3));

                    if (exitCode != 0 && !cancellationToken.IsCancellationRequested)
                    {
                        await crashReportService.PrintCrashReportAsync(crashHookService, unchecked((uint)process.Id), crashTime);
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        appLauncherService.TerminatePackageProcesses(packageFullName, unchecked((uint)process.Id));
                    }
                    return exitCode;
                }

                // Non-debug path: start crash hook reader and wait for exit
                crashHookService.StartReading();

                try
                {
                    await process.WaitForExitAsync(cancellationToken);
                    await crashHookService.WaitForCompletionAsync(TimeSpan.FromSeconds(3));

                    if (process.ExitCode != 0)
                    {
                        await crashReportService.PrintCrashReportAsync(crashHookService, unchecked((uint)process.Id), DateTime.UtcNow);
                    }

                    return process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    // Ctrl+C — terminate all processes belonging to the package before exiting.
                    appLauncherService.TerminatePackageProcesses(packageFullName, unchecked((uint)process.Id));
                    return -1;
                }
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Failed to launch via execution alias '{Alias}': {Error}", UiSymbols.Error, alias, ex.Message);
                return 1;
            }
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
