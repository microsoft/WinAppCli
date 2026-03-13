// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class PackageCommand : Command, IShortDescription
{
    public string ShortDescription => "Create MSIX package";

    public static Argument<DirectoryInfo?> InputFolderArgument { get; }
    public static Option<FileInfo> OutputOption { get; }
    public static Option<string?> NameOption { get; }
    public static Option<bool> SkipPriOption { get; }
    public static Option<FileInfo> CertOption { get; }
    public static Option<string> CertPasswordOption { get; }
    public static Option<bool> GenerateCertOption { get; }
    public static Option<bool> InstallCertOption { get; }
    public static Option<string?> PublisherOption { get; }
    public static Option<FileInfo> ManifestOption { get; }
    public static Option<bool> SelfContainedOption { get; }
    public static Option<string?> ExecutableOption { get; }

    // MCP Bundle options
    public static Option<FileInfo?> McpbOption { get; }
    public static Option<string> ArchitectureOption { get; }
    public static Option<string?> RuntimePathOption { get; }

    static PackageCommand()
    {
        InputFolderArgument = new Argument<DirectoryInfo?>("input-folder")
        {
            Description = "Input folder with package layout (not required when --mcpb is used)",
            Arity = ArgumentArity.ZeroOrOne
        };
        OutputOption = new Option<FileInfo>("--output")
        {
            Description = "Output msix file name for the generated package (defaults to <name>.msix)",
        };

        NameOption = new Option<string?>("--name")
        {
            Description = "Package name (default: from manifest)"
        };
        SkipPriOption = new Option<bool>("--skip-pri")
        {
            Description = "Skip PRI file generation"
        };
        CertOption = new Option<FileInfo>("--cert")
        {
            Description = "Path to signing certificate (will auto-sign if provided)"
        };
        CertOption.AcceptExistingOnly();
        CertPasswordOption = new Option<string>("--cert-password")
        {
            Description = "Certificate password (default: password)",
            DefaultValueFactory = (argumentResult) => "password"
        };
        GenerateCertOption = new Option<bool>("--generate-cert")
        {
            Description = "Generate a new development certificate"
        };
        InstallCertOption = new Option<bool>("--install-cert")
        {
            Description = "Install certificate to machine"
        };
        PublisherOption = new Option<string?>("--publisher")
        {
            Description = "Publisher name for certificate generation"
        };
        ManifestOption = new Option<FileInfo>("--manifest")
        {
            Description = "Path to AppX manifest file (default: auto-detect from input folder or current directory)"
        };
        ManifestOption.AcceptExistingOnly();
        SelfContainedOption = new Option<bool>("--self-contained")
        {
            Description = "Bundle Windows App SDK runtime for self-contained deployment"
        };
        ExecutableOption = new Option<string?>("--executable")
        {
            Description = "Path to the executable relative to the input folder."
        };
        ExecutableOption.Aliases.Add("--exe");

        // MCP Bundle options
        McpbOption = new Option<FileInfo?>("--mcpb")
        {
            Description = "Path to an MCP Bundle (.mcpb) file. Extracts, validates, and converts to MSIX with MCP server registration. When used, input-folder is not required."
        };
#pragma warning disable CS8620 // Nullability mismatch is intentional: McpbOption is nullable because it's optional
        McpbOption.AcceptExistingOnly();
#pragma warning restore CS8620
        ArchitectureOption = new Option<string>("--architecture")
        {
            Description = "Target processor architecture for MCP Bundle conversion (x64, x86, arm64). Default: x64."
        };
        ArchitectureOption.Aliases.Add("--arch");
        ArchitectureOption.AcceptOnlyFromAmong("x64", "x86", "arm64");
        ArchitectureOption.DefaultValueFactory = _ => "x64";
        RuntimePathOption = new Option<string?>("--runtime-path")
        {
            Description = "Path to the runtime executable (e.g., node.exe, python.exe) for script-based MCP servers. Auto-detected if not specified."
        };
    }

    public PackageCommand()
        : base("package", "Create MSIX installer from your built app or MCP Bundle. For apps: provide input-folder with appxmanifest.xml. For MCP servers: use --mcpb <file.mcpb> to convert an MCP Bundle to a signed MSIX with Windows ODR registration. Example: winapp package ./dist --cert devcert.pfx | winapp package --mcpb server.mcpb --generate-cert")
    {
        Aliases.Add("pack");
        Arguments.Add(InputFolderArgument);
        Options.Add(OutputOption);
        Options.Add(NameOption);
        Options.Add(SkipPriOption);
        Options.Add(CertOption);
        Options.Add(CertPasswordOption);
        Options.Add(GenerateCertOption);
        Options.Add(InstallCertOption);
        Options.Add(PublisherOption);
        Options.Add(ManifestOption);
        Options.Add(SelfContainedOption);
        Options.Add(ExecutableOption);
        Options.Add(McpbOption);
        Options.Add(ArchitectureOption);
        Options.Add(RuntimePathOption);
    }

    public class Handler(IMsixService msixService, IMcpbService mcpbService, IStatusService statusService) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var inputFolder = parseResult.GetValue(InputFolderArgument);
            var output = parseResult.GetValue(OutputOption);
            var name = parseResult.GetValue(NameOption);
            var skipPri = parseResult.GetValue(SkipPriOption);
            var certPath = parseResult.GetValue(CertOption);
            var certPassword = parseResult.GetRequiredValue(CertPasswordOption);
            var generateCert = parseResult.GetValue(GenerateCertOption);
            var installCert = parseResult.GetValue(InstallCertOption);
            var publisher = parseResult.GetValue(PublisherOption);
            var manifestPath = parseResult.GetValue(ManifestOption);
            var selfContained = parseResult.GetValue(SelfContainedOption);
            var executable = parseResult.GetValue(ExecutableOption);

            // MCP Bundle options
            var mcpbPath = parseResult.GetValue(McpbOption);
            var architecture = parseResult.GetRequiredValue(ArchitectureOption);
            var runtimePath = parseResult.GetValue(RuntimePathOption);

            // MCPB flow: convert .mcpb → staging directory → MSIX
            if (mcpbPath != null)
            {
                return await statusService.ExecuteWithStatusAsync("Converting MCP Bundle to MSIX...", async (taskContext, cancellationToken) =>
                {
                    McpbConversionResult? mcpbResult = null;
                    try
                    {
                        var publisherName = publisher ?? "CN=McpbToMsix-TestPublisher";

                        mcpbResult = await mcpbService.ExtractAndPrepareAsync(
                            mcpbPath, architecture, publisherName, runtimePath, taskContext, cancellationToken);

                        taskContext.AddStatusMessage($"{UiSymbols.Check} MCPB extracted and validated: {mcpbResult.DisplayName}");

                        // Use the staging directory as input for the standard MSIX packaging pipeline
                        var autoSign = certPath != null || generateCert;
                        var result = await msixService.CreateMsixPackageAsync(
                            mcpbResult.StagingDirectory,
                            output,
                            taskContext,
                            name ?? mcpbResult.PackageName,
                            skipPri: true, // MCP server packages don't need PRI
                            autoSign,
                            certPath,
                            certPassword,
                            generateCert,
                            installCert,
                            publisherName,
                            manifestPath: null, // manifest is already in the staging dir
                            selfContained: false,
                            mcpbResult.EntryPointExe,
                            cancellationToken);

                        taskContext.AddStatusMessage($"{UiSymbols.Package} Package: {result.MsixPath}");
                        if (result.Signed)
                        {
                            taskContext.AddStatusMessage($"{UiSymbols.Lock} Package has been signed");
                        }

                        return (0, "MCP Bundle conversion completed.");
                    }
                    catch (Exception ex)
                    {
                        taskContext.AddDebugMessage($"Stack Trace: {ex.StackTrace}");
                        return (1, $"{UiSymbols.Error} Failed to convert MCP Bundle: {ex.GetBaseException().Message}");
                    }
                    finally
                    {
                        // Clean up staging directory
                        if (mcpbResult?.StagingDirectory is { Exists: true } staging)
                        {
                            try { staging.Delete(recursive: true); } catch { /* best effort */ }
                        }
                    }
                }, cancellationToken);
            }

            // Standard flow: input-folder is required
            if (inputFolder is null || !inputFolder.Exists)
            {
                return await statusService.ExecuteWithStatusAsync("Creating MSIX package...", (taskContext, _) =>
                {
                    return Task.FromResult<(int, string)>((1, $"{UiSymbols.Error} input-folder is required when --mcpb is not used. Provide a directory with your app layout."));
                }, cancellationToken);
            }

            return await statusService.ExecuteWithStatusAsync("Creating MSIX package...", async (taskContext, cancellationToken) =>
            {
                try
                {
                    // Auto-sign if certificate is provided or if generate-cert is specified
                    var autoSign = certPath != null || generateCert;

                    var result = await msixService.CreateMsixPackageAsync(inputFolder, output, taskContext, name, skipPri, autoSign, certPath, certPassword, generateCert, installCert, publisher, manifestPath, selfContained, executable, cancellationToken);

                    taskContext.AddStatusMessage($"{UiSymbols.Package} Package: {result.MsixPath}");
                    if (result.Signed)
                    {
                        taskContext.AddStatusMessage($"{UiSymbols.Lock} Package has been signed");
                    }

                    return (0, "MSIX package creation completed.");
                }
                catch (Exception ex)
                {
                    taskContext.AddDebugMessage($"Stack Trace: {ex.StackTrace}");
                    return (1, $"{UiSymbols.Error} Failed to create MSIX package: {ex.GetBaseException().Message}");
                }
            }, cancellationToken);
        }
    }
}
