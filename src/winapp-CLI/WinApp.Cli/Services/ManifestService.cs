// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal partial class ManifestService(
    IManifestTemplateService manifestTemplateService,
    IImageAssetService imageAssetService,
    IAnsiConsole ansiConsole) : IManifestService
{
    public async Task<ManifestGenerationInfo> PromptForManifestInfoAsync(
        DirectoryInfo directory,
        string? packageName,
        string? publisherName,
        string version,
        string? description,
        string? executable,
        bool useDefaults,
        CancellationToken cancellationToken = default)
    {
        // Interactive mode if not --use-defaults (get defaults for prompts)
        if (!string.IsNullOrEmpty(executable))
        {
            var fileVersionInfo = FileVersionInfo.GetVersionInfo(executable);
            packageName ??= !string.IsNullOrWhiteSpace(fileVersionInfo.FileDescription)
                ? fileVersionInfo.FileDescription
                : Path.GetFileNameWithoutExtension(executable);
            if (!string.IsNullOrWhiteSpace(fileVersionInfo.Comments))
            {
                description = fileVersionInfo.Comments;
            }
            if (string.IsNullOrWhiteSpace(description) || description == packageName)
            {
                description = fileVersionInfo.FileDescription;
            }
            if (!string.IsNullOrWhiteSpace(fileVersionInfo.CompanyName))
            {
                publisherName ??= fileVersionInfo.CompanyName;
            }
        }
        packageName ??= SystemDefaultsHelper.GetDefaultPackageName(directory);
        description ??= SystemDefaultsHelper.GetDefaultDescription();
        publisherName ??= SystemDefaultsHelper.GetDefaultPublisherCN();

        packageName = CleanPackageName(packageName);

        // Interactive mode if not --use-defaults
        if (!useDefaults)
        {
            packageName = await PromptForValueAsync(ansiConsole, "Package name", packageName, cancellationToken);
            publisherName = await PromptForValueAsync(ansiConsole, "Publisher name", publisherName, cancellationToken);
            version = await PromptForValueAsync(ansiConsole, "Version", version, cancellationToken);
            description = await PromptForValueAsync(ansiConsole, "Description", description, cancellationToken);
        }

        return new ManifestGenerationInfo(
            packageName,
            publisherName,
            version,
            description);
    }

    public async Task GenerateManifestAsync(
        DirectoryInfo directory,
        ManifestGenerationInfo manifestGenerationInfo,
        ManifestTemplates manifestTemplate,
        FileInfo? logoPath,
        string? executable,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        taskContext.AddDebugMessage($"Generating manifest in directory: {directory}");

        string? packageName = manifestGenerationInfo.PackageName;
        string? publisherName = manifestGenerationInfo.PublisherName;
        string version = manifestGenerationInfo.Version;
        string description = manifestGenerationInfo.Description;

        taskContext.AddDebugMessage($"Logo path: {logoPath?.FullName ?? "None"}");

        packageName = CleanPackageName(packageName);

        // Resolve executable path if provided (used for icon extraction)
        string? executableAbsolute = null;
        if (!string.IsNullOrEmpty(executable))
        {
            executableAbsolute = Path.IsPathRooted(executable)
                ? executable
                : Path.GetFullPath(Path.Combine(directory.FullName, executable));

            executable = Path.GetRelativePath(directory.FullName, executableAbsolute);
        }

        // Generate complete manifest using shared service
        await manifestTemplateService.GenerateCompleteManifestAsync(
            directory,
            packageName,
            publisherName,
            version,
            manifestTemplate,
            description,
            taskContext,
            cancellationToken);

        string? extractedLogoPath = null;

        // If no logo provided, try to extract from executable (when available)
        if (logoPath == null && !string.IsNullOrEmpty(executableAbsolute))
        {
            taskContext.AddDebugMessage($"No logo path provided, attempting to extract from executable: {executableAbsolute}");
            Icon? extractedIcon = null;
            try
            {
                extractedIcon = ShellIcon.GetJumboIcon(executableAbsolute);
                // save temporary
                if (extractedIcon != null)
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempDir);

                    extractedLogoPath = Path.Combine(tempDir, "StoreLogo.png");
                    using (var stream = new FileStream(extractedLogoPath, FileMode.Create))
                    {
                        extractedIcon.ToBitmap().Save(stream, ImageFormat.Png);
                    }

                    logoPath = new FileInfo(extractedLogoPath);
                    taskContext.AddDebugMessage($"Extracted logo path: {logoPath.FullName}");
                }
            }
            finally
            {
                if (extractedIcon != null)
                {
                    extractedIcon.Dispose();
                }
            }
        }


        // If logo path is provided, update manifest assets
        if (logoPath?.Exists == true)
        {
            var manifestPath = new FileInfo(Path.Combine(directory.FullName, "Package.appxmanifest"));
            if (!manifestPath.Exists)
            {
                manifestPath = new FileInfo(Path.Combine(directory.FullName, "appxmanifest.xml"));
            }
            if (manifestPath.Exists)
            {
                await UpdateManifestAssetsAsync(manifestPath, logoPath, taskContext, cancellationToken: cancellationToken);
            }
        }

        if (extractedLogoPath != null)
        {
            // Clean up temporary extracted logo
            try
            {
                File.Delete(extractedLogoPath);
                Directory.Delete(Path.GetDirectoryName(extractedLogoPath)!);
            }
            catch (Exception ex)
            {
                taskContext.AddDebugMessage($"Could not delete temporary extracted logo: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Cleans and sanitizes a package name to meet MSIX AppxManifest Identity Name schema requirements.
    /// The Identity Name must match the pattern [-.A-Za-z0-9]+ (only letters, digits, periods, and hyphens).
    /// </summary>
    /// <param name="packageName">The package name to clean</param>
    /// <returns>A cleaned package name that meets MSIX Identity Name schema requirements</returns>
    internal static string CleanPackageName(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return "DefaultPackage";
        }

        // Trim whitespace
        var cleaned = packageName.Trim();

        // Remove invalid characters (keep only letters, numbers, hyphens, and periods)
        // MSIX Identity Name schema requires: [-.A-Za-z0-9]+
        // The regex below matches characters NOT in that set for removal
        cleaned = InvalidPackageNameCharRegex().Replace(cleaned, "");

        // If empty or whitespace after cleaning, use default
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "DefaultPackage";
        }

        // Ensure minimum length of 3 characters
        if (cleaned.Length < 3)
        {
            cleaned = cleaned.PadRight(3, '1'); // Pad with '1' to reach minimum length
        }

        // Truncate to maximum length of 50 characters
        if (cleaned.Length > 50)
        {
            cleaned = cleaned[..50].TrimEnd(); // Trim end in case we cut off mid-word
        }

        return cleaned;
    }

    private static async Task<string> PromptForValueAsync(IAnsiConsole ansiConsole, string prompt, string defaultValue, CancellationToken cancellationToken)
    {
        var result = await ansiConsole.PromptAsync(
            new TextPrompt<string>(prompt)
                .AllowEmpty()
                .DefaultValue(defaultValue)
                .ShowDefaultValue(),
            cancellationToken);

        ansiConsole.Cursor.MoveUp();
        ansiConsole.Write("\x1b[2K"); // Clear line
        ansiConsole.MarkupLine($"{prompt}: [underline]{result}[/]");

        return result;
    }

    [GeneratedRegex(@"[^A-Za-z0-9.\-]")]
    private static partial Regex InvalidPackageNameCharRegex();

    public async Task UpdateManifestAssetsAsync(
        FileInfo manifestPath,
        FileInfo imagePath,
        TaskContext taskContext,
        FileInfo? lightImagePath = null,
        CancellationToken cancellationToken = default)
    {
        taskContext.AddStatusMessage($"{UiSymbols.Info} Updating assets for manifest: {manifestPath.FullName}");

        var manifestDir = manifestPath.Directory;
        if (manifestDir == null)
        {
            throw new InvalidOperationException("Could not determine manifest directory");
        }

        var assetReferences = ExtractAssetReferencesFromManifest(manifestPath, taskContext);
        DirectoryInfo assetsDir;

        if (assetReferences.Count > 0)
        {
            await imageAssetService.GenerateAssetsFromManifestAsync(imagePath, manifestDir, assetReferences, taskContext, lightImagePath, cancellationToken);

            // Place app.ico alongside the app icon asset (44x44), falling back to
            // the most common asset directory so we don't depend on parse order.
            var appIconRef = assetReferences.FirstOrDefault(r => r.BaseWidth == 44 && r.BaseHeight == 44);
            var relativeAssetsDirectory = Path.GetDirectoryName(
                appIconRef?.RelativePath ?? GetMostCommonAssetDirectory(assetReferences));
            var assetsDirectoryPath = string.IsNullOrWhiteSpace(relativeAssetsDirectory)
                ? manifestDir.FullName
                : Path.Combine(manifestDir.FullName, relativeAssetsDirectory);
            assetsDir = new DirectoryInfo(assetsDirectoryPath);
        }
        else
        {
            taskContext.AddStatusMessage($"{UiSymbols.Warning} No asset references found in manifest, generating default assets");
            assetsDir = manifestDir.CreateSubdirectory("Assets");
            await imageAssetService.GenerateAssetsAsync(imagePath, assetsDir, taskContext, lightImagePath, cancellationToken);
        }

        if (!assetsDir.Exists)
        {
            assetsDir.Create();
        }

        var icoPath = DetermineIcoOutputPath(assetsDir, taskContext);
        await imageAssetService.GenerateIcoAsync(imagePath, icoPath, taskContext, cancellationToken);
    }

    /// <summary>
    /// Extracts asset references from an AppxManifest.xml file.
    /// Parses the manifest to find Logo, Square150x150Logo, Square44x44Logo, Wide310x150Logo, 
    /// and other image asset attributes, then determines their expected dimensions.
    /// </summary>
    internal static List<ManifestAssetReference> ExtractAssetReferencesFromManifest(FileInfo manifestPath, TaskContext taskContext)
    {
        var assetReferences = new List<ManifestAssetReference>();

        try
        {
            var doc = new System.Xml.XmlDocument();
            doc.Load(manifestPath.FullName);

            var nsmgr = new System.Xml.XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace("m", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
            nsmgr.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10");

            // Known asset types and their base dimensions
            var assetTypeDimensions = new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase)
            {
                // Square logos (old naming)
                { "Square44x44Logo", (44, 44) },
                { "Square71x71Logo", (71, 71) },
                { "Square150x150Logo", (150, 150) },
                { "Square310x310Logo", (310, 310) },
                // Wide logos (old naming)
                { "Wide310x150Logo", (310, 150) },
                // New naming convention
                { "AppList", (44, 44) },
                { "SmallTile", (71, 71) },
                { "MedTile", (150, 150) },
                { "WideTile", (310, 150) },
                { "LargeTile", (310, 310) },
                // Store logo (typically 50x50)
                { "Logo", (50, 50) },
                { "StoreLogo", (50, 50) },
                // Splash screen
                { "SplashScreen", (620, 300) },
                // Badge logo
                { "BadgeLogo", (24, 24) },
                // Lock screen logo
                { "LockScreenLogo", (24, 24) },
            };

            var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Extract Logo from Properties
            var logoNode = doc.SelectSingleNode("//m:Properties/m:Logo", nsmgr);
            if (logoNode != null && !string.IsNullOrWhiteSpace(logoNode.InnerText))
            {
                var logoPath = logoNode.InnerText.Trim();
                if (!addedPaths.Contains(logoPath))
                {
                    // Determine dimensions from filename or use default Store logo size
                    var dimensions = GetDimensionsFromPath(logoPath, assetTypeDimensions);
                    assetReferences.Add(new ManifestAssetReference(logoPath, dimensions.Width, dimensions.Height));
                    addedPaths.Add(logoPath);
                    taskContext.AddDebugMessage($"  Found Logo: {logoPath} ({dimensions.Width}x{dimensions.Height})");
                }
            }

            // Extract from uap:VisualElements attributes
            var visualElementsNodes = doc.SelectNodes("//uap:VisualElements", nsmgr);
            if (visualElementsNodes != null)
            {
                foreach (System.Xml.XmlNode visualElements in visualElementsNodes)
                {
                    if (visualElements.Attributes == null)
                    {
                        continue;
                    }

                    foreach (System.Xml.XmlAttribute attr in visualElements.Attributes)
                    {
                        if (assetTypeDimensions.TryGetValue(attr.Name, out var dimensions) && !string.IsNullOrWhiteSpace(attr.Value))
                        {
                            var assetPath = attr.Value.Trim();
                            if (!addedPaths.Contains(assetPath))
                            {
                                assetReferences.Add(new ManifestAssetReference(assetPath, dimensions.Width, dimensions.Height));
                                addedPaths.Add(assetPath);
                                taskContext.AddDebugMessage($"  Found {attr.Name}: {assetPath} ({dimensions.Width}x{dimensions.Height})");
                            }
                        }
                    }
                }
            }

            // Extract from uap:DefaultTile attributes
            var defaultTileNodes = doc.SelectNodes("//uap:DefaultTile", nsmgr);
            if (defaultTileNodes != null)
            {
                foreach (System.Xml.XmlNode defaultTile in defaultTileNodes)
                {
                    if (defaultTile.Attributes == null)
                    {
                        continue;
                    }

                    foreach (System.Xml.XmlAttribute attr in defaultTile.Attributes)
                    {
                        if (assetTypeDimensions.TryGetValue(attr.Name, out var dimensions) && !string.IsNullOrWhiteSpace(attr.Value))
                        {
                            var assetPath = attr.Value.Trim();
                            if (!addedPaths.Contains(assetPath))
                            {
                                assetReferences.Add(new ManifestAssetReference(assetPath, dimensions.Width, dimensions.Height));
                                addedPaths.Add(assetPath);
                                taskContext.AddDebugMessage($"  Found {attr.Name}: {assetPath} ({dimensions.Width}x{dimensions.Height})");
                            }
                        }
                    }
                }
            }

            // Extract from uap:SplashScreen attributes
            var splashScreenNodes = doc.SelectNodes("//uap:SplashScreen", nsmgr);
            if (splashScreenNodes != null)
            {
                foreach (System.Xml.XmlNode splashScreen in splashScreenNodes)
                {
                    var imageAttr = splashScreen.Attributes?["Image"];
                    if (imageAttr != null && !string.IsNullOrWhiteSpace(imageAttr.Value))
                    {
                        var assetPath = imageAttr.Value.Trim();
                        if (!addedPaths.Contains(assetPath))
                        {
                            var dimensions = assetTypeDimensions["SplashScreen"];
                            assetReferences.Add(new ManifestAssetReference(assetPath, dimensions.Width, dimensions.Height));
                            addedPaths.Add(assetPath);
                            taskContext.AddDebugMessage($"  Found SplashScreen: {assetPath} ({dimensions.Width}x{dimensions.Height})");
                        }
                    }
                }
            }

            // Extract from uap:LockScreen attributes
            var lockScreenNodes = doc.SelectNodes("//uap:LockScreen", nsmgr);
            if (lockScreenNodes != null)
            {
                foreach (System.Xml.XmlNode lockScreen in lockScreenNodes)
                {
                    var badgeLogoAttr = lockScreen.Attributes?["BadgeLogo"];
                    if (badgeLogoAttr != null && !string.IsNullOrWhiteSpace(badgeLogoAttr.Value))
                    {
                        var assetPath = badgeLogoAttr.Value.Trim();
                        if (!addedPaths.Contains(assetPath))
                        {
                            var dimensions = assetTypeDimensions["BadgeLogo"];
                            assetReferences.Add(new ManifestAssetReference(assetPath, dimensions.Width, dimensions.Height));
                            addedPaths.Add(assetPath);
                            taskContext.AddDebugMessage($"  Found BadgeLogo: {assetPath} ({dimensions.Width}x{dimensions.Height})");
                        }
                    }
                }
            }

            taskContext.AddDebugMessage($"Extracted {assetReferences.Count} asset references from manifest");
        }
        catch (Exception ex)
        {
            taskContext.AddDebugMessage($"Error parsing manifest for asset references: {ex.Message}");
        }

        return assetReferences;
    }

    /// <summary>
    /// Attempts to determine asset dimensions from the file path/name.
    /// Parses patterns like "Square150x150Logo.png" or "Wide310x150Logo.png".
    /// </summary>
    private static (int Width, int Height) GetDimensionsFromPath(string path, Dictionary<string, (int Width, int Height)> knownDimensions)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);

        // Check if the filename matches any known asset type
        foreach (var kvp in knownDimensions)
        {
            if (fileName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        // Try to parse dimensions from filename pattern like "Square150x150" or "Wide310x150"
        var match = DimensionRegex().Match(fileName);
        if (match.Success)
        {
            if (int.TryParse(match.Groups[1].Value, out var width) &&
                int.TryParse(match.Groups[2].Value, out var height))
            {
                return (width, height);
            }
        }

        // Default to store logo size
        return (50, 50);
    }

    [GeneratedRegex(@"(\d+)x(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DimensionRegex();


    [GeneratedRegex(@"^(\s*)<([\w:.-]+)((?:\s+[\w:.-]+\s*=\s*""[^""]*"")+)\s*(\/?>)\s*$")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"([\w:.-]+\s*=\s*""[^""]*"")")]
    private static partial Regex AttrPattern();

    public async Task<AddExecutionAliasResult> AddExecutionAliasAsync(
        AddExecutionAliasOptions options,
        CancellationToken cancellationToken = default)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(options.ManifestFile.FullName);
        }
        catch (Exception ex)
        {
            return new AddExecutionAliasResult(AddExecutionAliasStatus.ManifestParseError, ErrorMessage: ex.Message);
        }

        var root = doc.Root;
        if (root == null)
        {
            return new AddExecutionAliasResult(AddExecutionAliasStatus.ManifestEmpty);
        }

        // Find the target Application element
        var applications = root.Descendants(AppxManifestDocument.DefaultNs + "Application").ToList();
        if (applications.Count == 0)
        {
            return new AddExecutionAliasResult(AddExecutionAliasStatus.NoApplicationElement);
        }

        XElement targetApp;
        if (!string.IsNullOrEmpty(options.AppId))
        {
            targetApp = applications.FirstOrDefault(a =>
                string.Equals(a.Attribute("Id")?.Value, options.AppId, StringComparison.OrdinalIgnoreCase))!;
            if (targetApp == null)
            {
                return new AddExecutionAliasResult(AddExecutionAliasStatus.ApplicationIdNotFound);
            }
        }
        else
        {
            targetApp = applications[0];
        }

        // Infer alias name from Executable attribute if not specified
        var aliasName = options.AliasName;
        if (string.IsNullOrEmpty(aliasName))
        {
            var executable = targetApp.Attribute("Executable")?.Value;
            if (!string.IsNullOrEmpty(executable))
            {
                aliasName = executable;
            }
            else
            {
                return new AddExecutionAliasResult(AddExecutionAliasStatus.CouldNotInferAlias);
            }
        }

        // Ensure alias ends with .exe
        if (!aliasName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            aliasName += ".exe";
        }

        // Check if the target Application already has any execution alias
        var targetExtensions = targetApp.Element(AppxManifestDocument.DefaultNs + "Extensions");
        if (targetExtensions != null)
        {
            var existingAliasElements = targetExtensions
                .Elements(AppxManifestDocument.Uap5Ns + "Extension")
                .Where(e => string.Equals(e.Attribute("Category")?.Value, "windows.appExecutionAlias", StringComparison.OrdinalIgnoreCase))
                .Descendants(AppxManifestDocument.Uap5Ns + "ExecutionAlias")
                .Select(e => e.Attribute("Alias")?.Value)
                .Where(v => v != null)
                .ToList();

            if (existingAliasElements.Count > 0)
            {
                var existingAlias = existingAliasElements[0]!;
                if (string.Equals(existingAlias, aliasName, StringComparison.OrdinalIgnoreCase))
                {
                    return new AddExecutionAliasResult(AddExecutionAliasStatus.AlreadyExists, AliasName: aliasName);
                }
                else
                {
                    return new AddExecutionAliasResult(AddExecutionAliasStatus.ConflictingAliasExists, AliasName: aliasName, ExistingAlias: existingAlias);
                }
            }
        }

        // Ensure uap5 namespace is declared on the Package element
        if (root.GetNamespaceOfPrefix("uap5") == null)
        {
            root.Add(new XAttribute(XNamespace.Xmlns + "uap5", AppxManifestDocument.Uap5Ns));
        }

        // Ensure uap5 is in IgnorableNamespaces
        var ignorableAttr = root.Attribute("IgnorableNamespaces");
        if (ignorableAttr != null)
        {
            var namespaces = ignorableAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!namespaces.Contains("uap5", StringComparer.OrdinalIgnoreCase))
            {
                ignorableAttr.Value = ignorableAttr.Value + " uap5";
            }
        }

        // Build the ExecutionAlias element
        var aliasElement = new XElement(AppxManifestDocument.Uap5Ns + "ExecutionAlias",
            new XAttribute("Alias", aliasName));

        // Find or create the Extensions > uap5:Extension > uap5:AppExecutionAlias hierarchy
        var extensions = targetApp.Element(AppxManifestDocument.DefaultNs + "Extensions");
        if (extensions == null)
        {
            extensions = new XElement(AppxManifestDocument.DefaultNs + "Extensions");
            targetApp.Add(extensions);
        }

        // Look for an existing uap5:Extension with Category="windows.appExecutionAlias"
        var aliasExtension = extensions.Elements(AppxManifestDocument.Uap5Ns + "Extension")
            .FirstOrDefault(e => string.Equals(
                e.Attribute("Category")?.Value,
                "windows.appExecutionAlias",
                StringComparison.OrdinalIgnoreCase));

        if (aliasExtension != null)
        {
            // Add to existing AppExecutionAlias block
            var appExecAlias = aliasExtension.Element(AppxManifestDocument.Uap5Ns + "AppExecutionAlias");
            if (appExecAlias != null)
            {
                appExecAlias.Add(aliasElement);
            }
            else
            {
                var newAppExecAlias = new XElement(AppxManifestDocument.Uap5Ns + "AppExecutionAlias", aliasElement);
                aliasExtension.Add(newAppExecAlias);
            }
        }
        else
        {
            // Create new Extension block
            var newExtension = new XElement(AppxManifestDocument.Uap5Ns + "Extension",
                new XAttribute("Category", "windows.appExecutionAlias"),
                new XElement(AppxManifestDocument.Uap5Ns + "AppExecutionAlias", aliasElement));
            extensions.Add(newExtension);
        }

        // Save with UTF-8 no BOM and proper indentation
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = utf8NoBom,
            OmitXmlDeclaration = doc.Declaration == null,
        };

        // Write to memory first so we can post-process attribute formatting
        string xmlContent;
        using (var memoryStream = new MemoryStream())
        {
            using (var writer = XmlWriter.Create(memoryStream, settings))
            {
                doc.Save(writer);
            }

            xmlContent = utf8NoBom.GetString(memoryStream.ToArray());
        }

        // Split attributes onto separate lines for elements with more than 2 attributes
        xmlContent = FormatXmlAttributes(xmlContent);

        await File.WriteAllTextAsync(options.ManifestFile.FullName, xmlContent, utf8NoBom, cancellationToken);

        return new AddExecutionAliasResult(AddExecutionAliasStatus.Added, AliasName: aliasName);
    }

    /// <summary>
    /// Post-processes XML output to place each attribute on its own line
    /// when an element has more than 2 attributes, improving readability.
    /// </summary>
    internal static string FormatXmlAttributes(string xml)
    {
        var result = new StringBuilder();

        foreach (var rawLine in xml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var match = TagPattern().Match(line);
            if (match.Success)
            {
                var indent = match.Groups[1].Value;
                var tagName = match.Groups[2].Value;
                var attrsStr = match.Groups[3].Value;
                var closing = match.Groups[4].Value;

                var attrs = AttrPattern().Matches(attrsStr);
                if (attrs.Count > 2)
                {
                    var attrIndent = indent + "  ";
                    result.Append(indent).Append('<').Append(tagName);
                    foreach (Match attr in attrs)
                    {
                        result.Append(Environment.NewLine).Append(attrIndent).Append(attr.Value.Trim());
                    }

                    result.Append(closing == "/>" ? " />" : ">");
                    result.Append(Environment.NewLine);
                }
                else
                {
                    result.Append(line).Append(Environment.NewLine);
                }
            }
            else
            {
                result.Append(line).Append(Environment.NewLine);
            }
        }

        // Trim the trailing extra newline added by the loop
        var newLine = Environment.NewLine;
        if (result.Length >= newLine.Length)
        {
            result.Length -= newLine.Length;
        }

        return result.ToString();
    }

    /// <summary>
    /// Determines the output path for the generated ICO file.
    /// If the assets directory already contains an .ico file, reuses its name so that
    /// project-template icons (e.g. AppIcon.ico) are replaced rather than duplicated.
    /// When multiple .ico files exist, a name-based heuristic picks the most likely app icon.
    /// Falls back to "app.ico" when no existing .ico file is found.
    /// </summary>
    internal static string DetermineIcoOutputPath(DirectoryInfo assetsDir, TaskContext taskContext)
    {
        if (!assetsDir.Exists)
        {
            return Path.Combine(assetsDir.FullName, "app.ico");
        }

        var existingIcoFiles = assetsDir.GetFiles("*.ico");

        if (existingIcoFiles.Length == 0)
        {
            return Path.Combine(assetsDir.FullName, "app.ico");
        }

        if (existingIcoFiles.Length == 1)
        {
            taskContext.AddDebugMessage($"Found existing ICO file: {existingIcoFiles[0].Name}, will replace it");
            return existingIcoFiles[0].FullName;
        }

        // Multiple .ico files — pick the best candidate by name heuristic
        var preferredNames = new[] { "appicon", "app", "icon" };
        foreach (var preferred in preferredNames)
        {
            var match = existingIcoFiles.FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f.Name)
                    .Contains(preferred, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                taskContext.AddDebugMessage($"Found multiple ICO files, replacing best match: {match.Name}");
                return match.FullName;
            }
        }

        // No name heuristic matched — existing ICO files are likely unrelated,
        // so create app.ico rather than overwriting an unknown file.
        taskContext.AddDebugMessage($"Found {existingIcoFiles.Length} ICO files but none matched app icon heuristics, creating app.ico");
        return Path.Combine(assetsDir.FullName, "app.ico");
    }

    /// <summary>
    /// Returns the relative path of the asset whose parent directory appears most often,
    /// so the ICO file lands in the majority directory even for non-standard manifests.
    /// </summary>
    private static string GetMostCommonAssetDirectory(IReadOnlyList<ManifestAssetReference> assetReferences)
    {
        return assetReferences
            .GroupBy(r => Path.GetDirectoryName(r.RelativePath) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .First()
            .First()
            .RelativePath;
    }
}
