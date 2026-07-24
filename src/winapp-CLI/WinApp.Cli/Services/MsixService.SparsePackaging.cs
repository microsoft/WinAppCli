// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security;
using System.Text;
using System.Xml.Linq;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Sparse identity package production: creating the identity-only .msix from a sparse
/// manifest, resolving its output path, and embedding the &lt;msix&gt; identity into an app.
/// Split out of MsixService.Identity.cs (which retains the debug/loose-layout identity flow)
/// to keep each file within the repository's file-size guidance.
/// </summary>
internal partial class MsixService
{
    public async Task<CreateMsixPackageResult> CreateSparseIdentityPackageAsync(
        FileInfo manifestPath,
        FileSystemInfo? outputPath,
        TaskContext taskContext,
        bool autoSign = false,
        FileInfo? certificatePath = null,
        string certificatePassword = "password",
        bool generateDevCert = false,
        bool installDevCert = false,
        string? publisher = null,
        CancellationToken cancellationToken = default)
    {
        if (!manifestPath.Exists)
        {
            throw new FileNotFoundException($"Sparse manifest not found at: {manifestPath}");
        }

        var manifestContent = await File.ReadAllTextAsync(manifestPath.FullName, Encoding.UTF8, cancellationToken);
        var doc = AppxManifestDocument.Parse(manifestContent);

        if (!doc.AllowsExternalContent)
        {
            throw new InvalidOperationException(
                "The manifest does not declare <uap10:AllowExternalContent>true</uap10:AllowExternalContent> under <Properties>, so it is not a sparse identity package. " +
                "Generate one with 'winapp init --exe <exe> --sparse', or pass an input folder to package a full MSIX.");
        }

        var packageName = ManifestService.CleanPackageName(doc.IdentityName ?? "Package");
        var extractedPublisher = publisher ?? doc.IdentityPublisher;

        // Resolve output path: default to <PackageName>.identity.msix in the current directory.
        var defaultFileName = $"{packageName}.identity.msix";
        var (outputMsixPath, outputFolder) = ResolveSparseOutputPath(
            outputPath, defaultFileName, currentDirectoryProvider.GetCurrentDirectoryInfo());

        if (!outputFolder.Exists)
        {
            outputFolder.Create();
        }

        // Stage a directory containing ONLY the manifest. Sparse identity packages carry no
        // binaries or assets — those are resolved from the external content location at runtime.
        var stagingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"winapp-sparse-{Guid.NewGuid():N}"));
        stagingDir.Create();
        try
        {
            var stagedManifest = Path.Combine(stagingDir.FullName, "appxmanifest.xml");
            await File.WriteAllTextAsync(stagedManifest, manifestContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

            taskContext.AddDebugMessage($"{UiSymbols.Package} Packaging sparse identity manifest (external content): {manifestPath.Name}");

            await CreateMsixPackageFromFolderAsync(stagingDir, outputMsixPath, taskContext, cancellationToken);

            if (autoSign)
            {
                await SignMsixPackageAsync(outputFolder, certificatePassword, generateDevCert, installDevCert, packageName, extractedPublisher, outputMsixPath, certificatePath, manifestPath, taskContext, cancellationToken);
            }
        }
        finally
        {
            try
            {
                if (stagingDir.Exists)
                {
                    stagingDir.Delete(recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Could not clean up staging directory: {stagingDir.FullName} ({ex.Message})");
            }
        }

        return new CreateMsixPackageResult(outputMsixPath, autoSign);
    }

    /// <summary>
    /// Resolves where a sparse identity .msix should be written. An existing directory (even one
    /// whose name contains a dot) is used as the output folder; a non-existent path with an
    /// extension is treated as a file and must be '.msix' (bundles are rejected); anything else is
    /// treated as a target directory.
    /// </summary>
    internal static (FileInfo OutputMsix, DirectoryInfo OutputFolder) ResolveSparseOutputPath(
        FileSystemInfo? outputPath, string defaultFileName, DirectoryInfo currentDirectory)
    {
        // defaultFileName is a bare file name derived from the package name. Reject anything rooted or
        // nested so Path.Combine below can never drop the target directory and write somewhere else.
        if (string.IsNullOrWhiteSpace(defaultFileName)
            || Path.IsPathRooted(defaultFileName)
            || defaultFileName != Path.GetFileName(defaultFileName))
        {
            throw new InvalidOperationException(
                $"Internal error: sparse output file name must be a bare file name, not a path: '{defaultFileName}'.");
        }

        // Validated above to be a bare file name; use it consistently so the combines below are
        // unambiguously relative to the target directory.
        var safeFileName = Path.GetFileName(defaultFileName);

        if (outputPath == null)
        {
            return (new FileInfo(Path.Combine(currentDirectory.FullName, safeFileName)), currentDirectory);
        }

        if (Directory.Exists(outputPath.FullName))
        {
            // An existing directory is always treated as the output folder, even if its name
            // contains a dot (e.g. './release.v2') that Path.HasExtension would misread as a file.
            var dir = new DirectoryInfo(outputPath.FullName);
            return (new FileInfo(Path.Combine(dir.FullName, safeFileName)), dir);
        }

        if (Path.HasExtension(outputPath.Name))
        {
            // An extension on a non-existent path means the caller intends a file. Only .msix is
            // valid for a sparse identity package — reject .msixbundle and anything else rather
            // than silently creating a directory with that name.
            if (!string.Equals(Path.GetExtension(outputPath.Name), ".msix", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Invalid --output '{outputPath.Name}'. Sparse identity packages must be a single '.msix' file " +
                    "(bundles are not supported). Pass a '.msix' path or a directory.");
            }

            var file = new FileInfo(outputPath.FullName);
            return (file, file.Directory!);
        }

        var folder = new DirectoryInfo(outputPath.FullName);
        return (new FileInfo(Path.Combine(folder.FullName, safeFileName)), folder);
    }

    public async Task<MsixIdentityResult> EmbedIdentityAsync(
        FileInfo target,
        FileInfo manifestPath,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        if (!manifestPath.Exists)
        {
            throw new FileNotFoundException(
                $"AppX manifest not found at: {manifestPath}. Pass --manifest, or generate one with 'winapp init --exe <exe> --sparse'.");
        }

        var manifestContent = await File.ReadAllTextAsync(manifestPath.FullName, Encoding.UTF8, cancellationToken);

        // embed-identity connects an app to a sparse identity package (one registered with
        // 'Add-AppxPackage -ExternalLocation'), which requires AllowExternalContent. Refuse a
        // non-sparse manifest so we don't embed an identity that can never be registered that way.
        if (!AppxManifestDocument.Parse(manifestContent).AllowsExternalContent)
        {
            throw new InvalidOperationException(
                $"Manifest '{manifestPath.Name}' is not a sparse identity manifest (missing <uap10:AllowExternalContent>true). " +
                "embed-identity only applies to sparse packages registered with 'Add-AppxPackage -ExternalLocation'. " +
                "Generate one with 'winapp init --exe <exe> --sparse'.");
        }

        var identity = ParseAppxManifestAsync(manifestContent);

        var extension = target.Extension.ToLowerInvariant();
        if (extension == ".exe")
        {
            if (!target.Exists)
            {
                throw new FileNotFoundException($"Executable not found at: {target}");
            }

            taskContext.AddDebugMessage($"Embedding <msix> identity into exe fusion manifest: {target.Name}");
            await EmbedMsixIdentityToExeAsync(target, identity, taskContext, cancellationToken);
        }
        else
        {
            taskContext.AddDebugMessage($"Inserting <msix> identity into external SxS manifest: {target.Name}");
            await EmbedIdentityIntoXmlManifestAsync(target, identity, cancellationToken);
        }

        return identity;
    }

    /// <summary>
    /// Inserts or replaces the &lt;msix&gt; identity element in an external side-by-side manifest
    /// XML file. Creates a minimal assembly manifest if the target file does not yet exist.
    /// </summary>
    private static async Task EmbedIdentityIntoXmlManifestAsync(FileInfo target, MsixIdentityResult identity, CancellationToken cancellationToken)
    {
        XDocument xdoc;
        if (target.Exists)
        {
            var existing = await File.ReadAllTextAsync(target.FullName, cancellationToken);
            xdoc = XDocument.Parse(existing);
        }
        else
        {
            xdoc = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement(AsmV1Ns + "assembly", new XAttribute("manifestVersion", "1.0")));
        }

        var root = xdoc.Root
            ?? throw new InvalidOperationException($"The manifest '{target.FullName}' has no root element.");

        // Only touch genuine side-by-side (fusion) manifests. Refuse to append <msix> to an
        // arbitrary .xml file (e.g. app.config) that happens to be passed as the target.
        if (root.Name != AsmV1Ns + "assembly")
        {
            throw new InvalidOperationException(
                $"The target '{target.FullName}' is not a side-by-side manifest (its root is <{root.Name.LocalName}>, expected <assembly xmlns=\"{AsmV1Ns.NamespaceName}\">). " +
                "Point embed-identity at your app's .manifest/.xml side-by-side manifest.");
        }

        // Remove any existing <msix> element(s) so re-running the command is idempotent.
        root.Elements(MsixV1Ns + "msix").Remove();

        var msix = new XElement(MsixV1Ns + "msix",
            new XAttribute("publisher", identity.Publisher),
            new XAttribute("packageName", identity.PackageName),
            new XAttribute("applicationId", identity.ApplicationId));
        root.Add(msix);

        await using var stream = new FileStream(target.FullName, FileMode.Create, FileAccess.Write);
        await xdoc.SaveAsync(stream, SaveOptions.None, cancellationToken);
    }
}
