// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using System.Xml;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Services;

/// <summary>
/// Handles PRI (Package Resource Index) configuration, generation, and language extraction
/// via MakePri.exe.
/// </summary>
internal partial class PriService(
    IBuildToolsService buildToolsService) : IPriService
{
    // Extracts language tag from PRI dump qualifier strings like 'Language-en-US'
    [GeneratedRegex(@"qualifiers=""[^""]*Language-([a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8})*)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex PriDumpLanguageQualifierRegex();

    /// <summary>
    /// Creates a PRI configuration file for the given package directory
    /// </summary>
    public async Task<FileInfo> CreatePriConfigAsync(
        DirectoryInfo packageDir,
        TaskContext taskContext,
        IEnumerable<string> precomputedPriResourceCandidates,
        string language = "en-US",
        string platformVersion = "10.0.0",
        CancellationToken cancellationToken = default)
    {
        if (!packageDir.Exists)
        {
            throw new DirectoryNotFoundException($"Package directory not found: {packageDir}");
        }

        ArgumentNullException.ThrowIfNull(precomputedPriResourceCandidates);

        var resfilesPath = Path.Combine(packageDir.FullName, "pri.resfiles");
        var priResourceCandidates = precomputedPriResourceCandidates.ToList();

        priResourceCandidates = [.. priResourceCandidates
            .Where(path => MrtAssetHelper.PriIncludedExtensions.Contains(Path.GetExtension(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];

        taskContext.AddDebugMessage($"PRI resource candidates discovered: {priResourceCandidates.Count}");

        using (var writer = new StreamWriter(resfilesPath))
        {
            foreach (var priFile in priResourceCandidates)
            {
                await writer.WriteLineAsync(priFile);
            }
        }

        var configPath = new FileInfo(Path.Combine(packageDir.FullName, "priconfig.xml"));
        var arguments = $@"createconfig /cf ""{configPath}"" /dq lang-{language}_scale-200 /pv {platformVersion} /o";

        taskContext.AddDebugMessage("Creating PRI configuration file...");

        try
        {
            await buildToolsService.RunBuildToolAsync(new MakePriTool(), arguments, taskContext, cancellationToken: cancellationToken);

            taskContext.AddDebugMessage($"PRI configuration created: {configPath}");

            var xmlDoc = new XmlDocument();
            xmlDoc.Load(configPath.FullName);
            var resourcesNode = xmlDoc.SelectSingleNode("/resources");
            if (resourcesNode != null)
            {
                var indexNode = resourcesNode.SelectSingleNode("index");
                if (indexNode != null)
                {
                    if (indexNode.Attributes?["startIndexAt"]?.Value != null)
                    {
                        // set to relative path
                        indexNode.Attributes["startIndexAt"]!.Value = ".\\pri.resfiles";
                    }

                    var resfilesIndexerNode = xmlDoc.CreateElement("indexer-config");
                    var typeAttr = xmlDoc.CreateAttribute("type");
                    typeAttr.Value = "resfiles";
                    resfilesIndexerNode.Attributes.Append(typeAttr);

                    var delimiterAttr = xmlDoc.CreateAttribute("qualifierDelimiter");
                    delimiterAttr.Value = ".";
                    resfilesIndexerNode.Attributes.Append(delimiterAttr);

                    indexNode.AppendChild(resfilesIndexerNode);

                    // Ensure folder-based indexer is configured to parse qualifiers from
                    // both folder names and file names (e.g. targetsize-48_altform-unplated).
                    var folderIndexerNode = indexNode
                        .SelectNodes("indexer-config")
                        ?.OfType<XmlNode>()
                        .FirstOrDefault(node =>
                            node.Attributes?["type"]?.Value?.Equals("folder", StringComparison.OrdinalIgnoreCase) == true);

                    if (folderIndexerNode?.Attributes != null)
                    {
                        var folderAttributes = folderIndexerNode.Attributes;

                        var folderNameAsQualifierAttr = folderAttributes["foldernameAsQualifier"];
                        if (folderNameAsQualifierAttr == null)
                        {
                            folderNameAsQualifierAttr = xmlDoc.CreateAttribute("foldernameAsQualifier");
                            folderAttributes.Append(folderNameAsQualifierAttr);
                        }
                        folderNameAsQualifierAttr.Value = "true";

                        var fileNameAsQualifierAttr = folderAttributes["filenameAsQualifier"];
                        if (fileNameAsQualifierAttr == null)
                        {
                            fileNameAsQualifierAttr = xmlDoc.CreateAttribute("filenameAsQualifier");
                            folderAttributes.Append(fileNameAsQualifierAttr);
                        }
                        fileNameAsQualifierAttr.Value = "true";
                    }

                    xmlDoc.Save(configPath.FullName);
                }
            }

            return configPath;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create PRI configuration: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Generates a PRI file from the configuration
    /// </summary>
    public async Task<List<FileInfo>> GeneratePriFileAsync(DirectoryInfo packageDir, TaskContext taskContext, FileInfo? configPath = null, FileInfo? outputPath = null, CancellationToken cancellationToken = default)
    {
        if (!packageDir.Exists)
        {
            throw new DirectoryNotFoundException($"Package directory not found: {packageDir}");
        }

        var priConfigPath = configPath ?? new FileInfo(Path.Combine(packageDir.FullName, "priconfig.xml"));
        var priOutputPath = outputPath ?? new FileInfo(Path.Combine(packageDir.FullName, "resources.pri"));

        if (!priConfigPath.Exists)
        {
            throw new FileNotFoundException($"PRI configuration file not found: {priConfigPath}");
        }

        var arguments = $@"new /pr ""{Path.TrimEndingDirectorySeparator(packageDir.FullName)}"" /cf ""{priConfigPath.FullName}"" /of ""{priOutputPath.FullName}"" /o";

        taskContext.AddDebugMessage("Generating PRI file...");

        try
        {
            var (stdout, stderr) = await buildToolsService.RunBuildToolAsync(new MakePriTool(), arguments, taskContext, cancellationToken: cancellationToken);

            // Parse the output to extract resource files
            var resourceFiles = new List<FileInfo>();
            var lines = stdout.Replace("\0", "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                // Look for lines that match the pattern "Resource File: *"
                const string resourceFileStr = "Resource File: ";
                if (line.StartsWith(resourceFileStr, StringComparison.OrdinalIgnoreCase))
                {
                    var fileName = line[resourceFileStr.Length..].Trim();
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        resourceFiles.Add(new FileInfo(Path.Combine(packageDir.FullName, fileName)));
                    }
                }
            }

            taskContext.AddDebugMessage($"PRI file generated: {priOutputPath}");
            if (resourceFiles.Count > 0)
            {
                taskContext.AddDebugMessage($"Processed {resourceFiles.Count} resource files");
            }

            return resourceFiles;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to generate PRI file: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Extracts language qualifiers from a PRI file using <c>makepri dump</c>.
    /// Returns a distinct, sorted list of BCP-47 language tags found in the PRI resource map.
    /// </summary>
    public async Task<List<string>> ExtractLanguagesFromPriAsync(
        FileInfo priFile,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var dumpOutputFile = Path.Combine(Path.GetTempPath(), $"winapp-pri-dump-{Guid.NewGuid():N}.xml");
            var arguments = $@"dump /if ""{priFile.FullName}"" /of ""{dumpOutputFile}"" /o";

            await buildToolsService.RunBuildToolAsync(new MakePriTool(), arguments, taskContext, cancellationToken: cancellationToken);

            if (!File.Exists(dumpOutputFile))
            {
                return [];
            }

            try
            {
                var dumpContent = await File.ReadAllTextAsync(dumpOutputFile, cancellationToken);

                // Extract language qualifiers from Candidate elements:
                // <Candidate qualifiers="Language-en-US" ...> or multi-qualifier like "Language-en-US, Scale-200"
                var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match match in PriDumpLanguageQualifierRegex().Matches(dumpContent))
                {
                    languages.Add(match.Groups[1].Value);
                }

                return languages.OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();
            }
            finally
            {
                File.Delete(dumpOutputFile);
            }
        }
        catch (Exception ex)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} Failed to extract languages from PRI: {ex.Message}");
            return [];
        }
    }
}
