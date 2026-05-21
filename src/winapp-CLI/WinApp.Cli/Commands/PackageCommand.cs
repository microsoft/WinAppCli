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

    public static Argument<DirectoryInfo[]> InputFolderArgument { get; }
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

    static PackageCommand()
    {
        InputFolderArgument = new Argument<DirectoryInfo[]>("input-folder")
        {
            Description = "One or more input folders with package layout. Pass multiple folders to create an MSIX bundle (e.g., winapp pack ./publish/x64 ./publish/arm64).",
            Arity = ArgumentArity.OneOrMore
        };
        OutputOption = new Option<FileInfo>("--output")
        {
            Description = "Output msix file name for the generated package (defaults to <name>_<version>_<arch>.msix, falling back to <name>_<version>.msix, <name>_<arch>.msix, or <name>.msix when version/arch can't be determined)",
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
    }

    public PackageCommand()
        : base("package", "Create MSIX installer from your built app. Run after building your app. A manifest (Package.appxmanifest or appxmanifest.xml) is required for packaging - it must be in current working directory, passed as --manifest or be in the input folder. Use --cert devcert.pfx to sign for testing. Example: winapp package ./dist --manifest Package.appxmanifest --cert ./devcert.pfx")
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
    }

    public class Handler(IMsixService msixService, IStatusService statusService) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var inputFolders = parseResult.GetRequiredValue(InputFolderArgument);
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

            // Validate all input folders exist (report all missing at once)
            var missingDirs = inputFolders.Where(d => !d.Exists).ToList();
            if (missingDirs.Count > 0)
            {
                var missingPaths = string.Join(Environment.NewLine, missingDirs.Select(d => $"  {d.FullName}"));
                return await statusService.ExecuteWithStatusAsync("Validating input...", (taskContext, _) =>
                {
                    return Task.FromResult((1, $"{UiSymbols.Error} Input folder(s) not found:{Environment.NewLine}{missingPaths}"));
                }, cancellationToken);
            }

            // Reject duplicate paths (normalize and compare)
            var normalizedPaths = inputFolders.Select(d => Path.GetFullPath(d.FullName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToList();
            var duplicates = normalizedPaths.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0)
            {
                var dupPaths = string.Join(Environment.NewLine, duplicates.Select(d => $"  {d}"));
                return await statusService.ExecuteWithStatusAsync("Validating input...", (taskContext, _) =>
                {
                    return Task.FromResult((1, $"{UiSymbols.Error} Duplicate input folder(s):{Environment.NewLine}{dupPaths}"));
                }, cancellationToken);
            }

            // Validate --output extension for bundle mode
            if (inputFolders.Length > 1 && output != null)
            {
                var ext = Path.GetExtension(output.Name);
                if (string.Equals(ext, ".msix", StringComparison.OrdinalIgnoreCase))
                {
                    return await statusService.ExecuteWithStatusAsync("Validating input...", (taskContext, _) =>
                    {
                        return Task.FromResult((1, $"{UiSymbols.Error} Cannot use .msix extension for --output when creating a bundle from multiple folders. Use .msixbundle or omit the extension."));
                    }, cancellationToken);
                }
            }

            if (inputFolders.Length == 1)
            {
                // Single folder: existing behavior unchanged
                var inputFolder = inputFolders[0];
                return await statusService.ExecuteWithStatusAsync("Creating MSIX package...", async (taskContext, cancellationToken) =>
                {
                    try
                    {
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
            else
            {
                // Multiple folders: create MSIX bundle
                return await statusService.ExecuteWithStatusAsync("Creating MSIX bundle...", async (taskContext, cancellationToken) =>
                {
                    try
                    {
                        var autoSign = certPath != null || generateCert;

                        var result = await msixService.CreateMsixBundleAsync(inputFolders, output, taskContext, name, skipPri, autoSign, certPath, certPassword, generateCert, installCert, publisher, manifestPath, selfContained, executable, cancellationToken);

                        taskContext.AddStatusMessage($"{UiSymbols.Package} Bundle: {result.BundlePath}");
                        if (result.Signed)
                        {
                            taskContext.AddStatusMessage($"{UiSymbols.Lock} Bundle has been signed");
                        }
                        else
                        {
                            taskContext.AddStatusMessage($"Bundle is unsigned. For Store submission, upload as-is. For sideload, run `winapp sign`.");
                        }

                        return (0, "MSIX bundle creation completed.");
                    }
                    catch (Exception ex)
                    {
                        taskContext.AddDebugMessage($"Stack Trace: {ex.StackTrace}");
                        return (1, $"{UiSymbols.Error} Failed to create MSIX bundle: {ex.GetBaseException().Message}");
                    }
                }, cancellationToken);
            }
        }
    }
}
