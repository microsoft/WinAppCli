// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal partial class ManifestAddAliasCommand : Command, IShortDescription
{
    public string ShortDescription => "Add an execution alias to the app manifest";

    public static Option<string> NameOption { get; }
    public static Option<FileInfo> ManifestOption { get; }
    public static Option<string> AppIdOption { get; }

    private static readonly XNamespace DefaultNs = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap5Ns = "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";

    static ManifestAddAliasCommand()
    {
        NameOption = new Option<string>("--name")
        {
            Description = "Alias name (e.g. 'myapp.exe'). Default: inferred from the Executable attribute in the manifest."
        };

        ManifestOption = new Option<FileInfo>("--manifest")
        {
            Description = "Path to AppxManifest.xml file (default: search current directory)"
        };
        ManifestOption.AcceptExistingOnly();

        AppIdOption = new Option<string>("--app-id")
        {
            Description = "Application Id to add the alias to (default: first Application element)"
        };
    }

    public ManifestAddAliasCommand() : base("add-alias", "Add an execution alias (uap5:AppExecutionAlias) to an appxmanifest.xml. " +
        "This allows launching the packaged app from the command line by typing the alias name. " +
        "By default, the alias is inferred from the Executable attribute (e.g. $targetnametoken$.exe becomes $targetnametoken$.exe alias).")
    {
        Options.Add(NameOption);
        Options.Add(ManifestOption);
        Options.Add(AppIdOption);
    }

    public partial class Handler(ICurrentDirectoryProvider currentDirectoryProvider, ILogger<ManifestAddAliasCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var aliasName = parseResult.GetValue(NameOption);
            var manifestFile = parseResult.GetValue(ManifestOption);
            var appId = parseResult.GetValue(AppIdOption);

            // Find manifest
            FileInfo? resolvedManifest = manifestFile;
            if (resolvedManifest == null)
            {
                resolvedManifest = MsixService.FindProjectManifest(currentDirectoryProvider);
                if (resolvedManifest == null || !resolvedManifest.Exists)
                {
                    logger.LogError("{UISymbol} Could not find appxmanifest.xml in the current directory. Use --manifest to specify the path.", UiSymbols.Error);
                    return 1;
                }
            }

            XDocument doc;
            try
            {
                doc = XDocument.Load(resolvedManifest.FullName);
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Failed to parse manifest: {Error}", UiSymbols.Error, ex.Message);
                return 1;
            }

            var root = doc.Root;
            if (root == null)
            {
                logger.LogError("{UISymbol} Manifest has no root element.", UiSymbols.Error);
                return 1;
            }

            // Find the target Application element
            var applications = root.Descendants(DefaultNs + "Application").ToList();
            if (applications.Count == 0)
            {
                logger.LogError("{UISymbol} No <Application> element found in the manifest.", UiSymbols.Error);
                return 1;
            }

            XElement targetApp;
            if (!string.IsNullOrEmpty(appId))
            {
                targetApp = applications.FirstOrDefault(a =>
                    string.Equals(a.Attribute("Id")?.Value, appId, StringComparison.OrdinalIgnoreCase))!;
                if (targetApp == null)
                {
                    logger.LogError("{UISymbol} No <Application> element with Id='{AppId}' found in the manifest.", UiSymbols.Error, appId);
                    return 1;
                }
            }
            else
            {
                targetApp = applications[0];
            }

            // Infer alias name from Executable attribute if not specified
            if (string.IsNullOrEmpty(aliasName))
            {
                var executable = targetApp.Attribute("Executable")?.Value;
                if (!string.IsNullOrEmpty(executable))
                {
                    aliasName = executable;
                }
                else
                {
                    logger.LogError("{UISymbol} Could not infer alias name from Executable attribute. Use --name to specify the alias.", UiSymbols.Error);
                    return 1;
                }
            }

            // Ensure alias ends with .exe
            if (!aliasName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                aliasName += ".exe";
            }

            // Check if the target Application already has any execution alias
            var targetExtensions = targetApp.Element(DefaultNs + "Extensions");
            if (targetExtensions != null)
            {
                var existingAliasElements = targetExtensions
                    .Elements(Uap5Ns + "Extension")
                    .Where(e => string.Equals(e.Attribute("Category")?.Value, "windows.appExecutionAlias", StringComparison.OrdinalIgnoreCase))
                    .Descendants(Uap5Ns + "ExecutionAlias")
                    .Select(e => e.Attribute("Alias")?.Value)
                    .Where(v => v != null)
                    .ToList();

                if (existingAliasElements.Count > 0)
                {
                    var existingAlias = existingAliasElements[0]!;
                    if (string.Equals(existingAlias, aliasName, StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogInformation("{UISymbol} Execution alias '{Alias}' already exists in the manifest.", UiSymbols.Warning, aliasName);
                        return 0;
                    }
                    else
                    {
                        logger.LogError("{UISymbol} Application already has an execution alias '{ExistingAlias}'. Only one execution alias per application is supported. Remove the existing alias first or use the same name.", UiSymbols.Error, existingAlias);
                        return 1;
                    }
                }
            }

            // Ensure uap5 namespace is declared on the Package element
            if (root.GetNamespaceOfPrefix("uap5") == null)
            {
                root.Add(new XAttribute(XNamespace.Xmlns + "uap5", Uap5Ns));
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
            var aliasElement = new XElement(Uap5Ns + "ExecutionAlias",
                new XAttribute("Alias", aliasName));

            // Find or create the Extensions > uap5:Extension > uap5:AppExecutionAlias hierarchy
            var extensions = targetApp.Element(DefaultNs + "Extensions");
            if (extensions == null)
            {
                extensions = new XElement(DefaultNs + "Extensions");
                targetApp.Add(extensions);
            }

            // Look for an existing uap5:Extension with Category="windows.appExecutionAlias"
            var aliasExtension = extensions.Elements(Uap5Ns + "Extension")
                .FirstOrDefault(e => string.Equals(
                    e.Attribute("Category")?.Value,
                    "windows.appExecutionAlias",
                    StringComparison.OrdinalIgnoreCase));

            if (aliasExtension != null)
            {
                // Add to existing AppExecutionAlias block
                var appExecAlias = aliasExtension.Element(Uap5Ns + "AppExecutionAlias");
                if (appExecAlias != null)
                {
                    appExecAlias.Add(aliasElement);
                }
                else
                {
                    var newAppExecAlias = new XElement(Uap5Ns + "AppExecutionAlias", aliasElement);
                    aliasExtension.Add(newAppExecAlias);
                }
            }
            else
            {
                // Create new Extension block
                var newExtension = new XElement(Uap5Ns + "Extension",
                    new XAttribute("Category", "windows.appExecutionAlias"),
                    new XElement(Uap5Ns + "AppExecutionAlias", aliasElement));
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

            await File.WriteAllTextAsync(resolvedManifest.FullName, xmlContent, utf8NoBom, cancellationToken);

            logger.LogInformation("{UISymbol} Added execution alias '{Alias}' to {Manifest}", UiSymbols.Check, aliasName, resolvedManifest.FullName);
            return 0;
        }

        /// <summary>
        /// Post-processes XML output to place each attribute on its own line
        /// when an element has more than 2 attributes, improving readability.
        /// </summary>
        [GeneratedRegex(@"^(\s*)<([\w:.-]+)((?:\s+[\w:.-]+\s*=\s*""[^""]*"")+)\s*(\/?>)\s*$")]
        private static partial Regex TagPattern();

        [GeneratedRegex(@"([\w:.-]+\s*=\s*""[^""]*"")" )]
        private static partial Regex AttrPattern();

        private static string FormatXmlAttributes(string xml)
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
    }
}
