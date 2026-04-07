// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace WinApp.Cli.Services;

/// <summary>
/// XDocument-based wrapper for reading and manipulating AppxManifest.xml files.
/// This is a pure data class with no DI dependencies.
/// </summary>
internal class AppxManifestDocument
{
    // AppxManifest XML namespaces
    public static readonly XNamespace DefaultNs = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    public static readonly XNamespace UapNs = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    public static readonly XNamespace Uap5Ns = "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";
    public static readonly XNamespace Uap10Ns = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
    public static readonly XNamespace RescapNs = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
    public static readonly XNamespace BuildNs = "http://schemas.microsoft.com/developer/appx/2015/build";
    public static readonly XNamespace DesktopNs = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
    public static readonly XNamespace Desktop6Ns = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/6";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly XDocument _document;

    private AppxManifestDocument(XDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
    }

    /// <summary>
    /// Direct access to the underlying XDocument for advanced operations.
    /// </summary>
    public XDocument Document => _document;

    #region Static Factory Methods

    /// <summary>
    /// Loads an AppxManifest from a file path.
    /// </summary>
    public static AppxManifestDocument Load(string path)
    {
        var doc = XDocument.Load(path);
        return new AppxManifestDocument(doc);
    }

    /// <summary>
    /// Loads an AppxManifest from a stream.
    /// </summary>
    public static AppxManifestDocument Load(Stream stream)
    {
        var doc = XDocument.Load(stream);
        return new AppxManifestDocument(doc);
    }

    /// <summary>
    /// Parses an AppxManifest from an XML string.
    /// </summary>
    public static AppxManifestDocument Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        return new AppxManifestDocument(doc);
    }

    #endregion

    #region Save / Serialize

    /// <summary>
    /// Saves the manifest to a file with UTF-8 (no BOM) encoding.
    /// </summary>
    public void Save(string path)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = Utf8NoBom,
            OmitXmlDeclaration = _document.Declaration == null,
        };

        using var memoryStream = new MemoryStream();
        using (var writer = XmlWriter.Create(memoryStream, settings))
        {
            _document.Save(writer);
        }

        File.WriteAllBytes(path, memoryStream.ToArray());
    }

    /// <summary>
    /// Serializes the manifest to an XML string.
    /// </summary>
    public string ToXml()
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = Utf8NoBom,
            OmitXmlDeclaration = _document.Declaration == null,
        };

        using var memoryStream = new MemoryStream();
        using (var writer = XmlWriter.Create(memoryStream, settings))
        {
            _document.Save(writer);
        }

        return Utf8NoBom.GetString(memoryStream.ToArray());
    }

    #endregion

    #region Element Accessors

    /// <summary>
    /// Gets the Identity element.
    /// </summary>
    public XElement? GetIdentityElement() =>
        _document.Root?.Element(DefaultNs + "Identity");

    /// <summary>
    /// Gets the first Application element.
    /// </summary>
    public XElement? GetFirstApplicationElement() =>
        _document.Root?.Element(DefaultNs + "Applications")?.Element(DefaultNs + "Application");

    /// <summary>
    /// Gets the uap:VisualElements element from the first Application.
    /// </summary>
    public XElement? GetVisualElements() =>
        GetFirstApplicationElement()?.Element(UapNs + "VisualElements");

    /// <summary>
    /// Gets the Resources element.
    /// </summary>
    public XElement? GetResourcesElement() =>
        _document.Root?.Element(DefaultNs + "Resources");

    /// <summary>
    /// Gets the Dependencies element.
    /// </summary>
    public XElement? GetDependenciesElement() =>
        _document.Root?.Element(DefaultNs + "Dependencies");

    /// <summary>
    /// Gets the package-level Extensions element (child of Package, after Applications).
    /// </summary>
    public XElement? GetExtensionsElement() =>
        _document.Root?.Element(DefaultNs + "Extensions");

    /// <summary>
    /// Gets the Capabilities element.
    /// </summary>
    public XElement? GetCapabilitiesElement() =>
        _document.Root?.Element(DefaultNs + "Capabilities");

    #endregion

    #region Identity Properties

    /// <summary>
    /// Gets or sets the Identity Name attribute.
    /// </summary>
    public string? IdentityName
    {
        get => GetIdentityElement()?.Attribute("Name")?.Value;
        set => SetIdentityAttribute("Name", value);
    }

    /// <summary>
    /// Gets or sets the Identity Publisher attribute.
    /// </summary>
    public string? IdentityPublisher
    {
        get => GetIdentityElement()?.Attribute("Publisher")?.Value;
        set => SetIdentityAttribute("Publisher", value);
    }

    /// <summary>
    /// Gets or sets the Identity Version attribute.
    /// </summary>
    public string? IdentityVersion
    {
        get => GetIdentityElement()?.Attribute("Version")?.Value;
        set => SetIdentityAttribute("Version", value);
    }

    /// <summary>
    /// Gets or sets the Identity ProcessorArchitecture attribute.
    /// </summary>
    public string? IdentityProcessorArchitecture
    {
        get => GetIdentityElement()?.Attribute("ProcessorArchitecture")?.Value;
        set => SetIdentityAttribute("ProcessorArchitecture", value);
    }

    private void SetIdentityAttribute(string attributeName, string? value)
    {
        var identity = GetIdentityElement();
        if (identity == null)
        {
            if (value == null)
            {
                return;
            }

            identity = new XElement(DefaultNs + "Identity");
            _document.Root?.AddFirst(identity);
        }

        if (value == null)
        {
            identity.Attribute(attributeName)?.Remove();
        }
        else
        {
            identity.SetAttributeValue(attributeName, value);
        }
    }

    #endregion

    #region Application Properties

    /// <summary>
    /// Gets or sets the first Application's Id attribute.
    /// </summary>
    public string? ApplicationId
    {
        get => GetFirstApplicationElement()?.Attribute("Id")?.Value;
        set => SetApplicationAttribute("Id", value);
    }

    /// <summary>
    /// Gets or sets the first Application's Executable attribute.
    /// </summary>
    public string? ApplicationExecutable
    {
        get => GetFirstApplicationElement()?.Attribute("Executable")?.Value;
        set => SetApplicationAttribute("Executable", value);
    }

    /// <summary>
    /// Gets or sets the first Application's EntryPoint attribute.
    /// </summary>
    public string? ApplicationEntryPoint
    {
        get => GetFirstApplicationElement()?.Attribute("EntryPoint")?.Value;
        set => SetApplicationAttribute("EntryPoint", value);
    }

    private void SetApplicationAttribute(string attributeName, string? value)
    {
        var app = GetFirstApplicationElement();
        if (app == null)
        {
            return;
        }

        if (value == null)
        {
            app.Attribute(attributeName)?.Remove();
        }
        else
        {
            app.SetAttributeValue(attributeName, value);
        }
    }

    #endregion

    #region VisualElements Properties

    /// <summary>
    /// Gets or sets the uap:VisualElements DisplayName attribute.
    /// </summary>
    public string? VisualElementsDisplayName
    {
        get => GetVisualElements()?.Attribute("DisplayName")?.Value;
        set
        {
            var ve = GetVisualElements();
            if (ve == null)
            {
                return;
            }

            if (value == null)
            {
                ve.Attribute("DisplayName")?.Remove();
            }
            else
            {
                ve.SetAttributeValue("DisplayName", value);
            }
        }
    }

    #endregion

    #region Resource Languages

    /// <summary>
    /// Extracts all Resource Language values.
    /// </summary>
    public List<string> GetResourceLanguages()
    {
        var resources = GetResourcesElement();
        if (resources == null)
        {
            return [];
        }

        return resources.Elements(DefaultNs + "Resource")
            .Select(r => r.Attribute("Language")?.Value)
            .Where(lang => lang != null)
            .Cast<string>()
            .ToList();
    }

    /// <summary>
    /// Replaces the Resources block with the given languages.
    /// </summary>
    public void SetResourceLanguages(IList<string> languages)
    {
        var root = _document.Root;
        if (root == null)
        {
            return;
        }

        var resources = GetResourcesElement();
        if (resources == null)
        {
            resources = new XElement(DefaultNs + "Resources");
            // Insert after Dependencies if present, otherwise after Identity
            var dependencies = GetDependenciesElement();
            if (dependencies != null)
            {
                dependencies.AddAfterSelf(resources);
            }
            else
            {
                var identity = GetIdentityElement();
                if (identity != null)
                {
                    identity.AddAfterSelf(resources);
                }
                else
                {
                    root.Add(resources);
                }
            }
        }
        else
        {
            resources.RemoveAll();
        }

        foreach (var lang in languages)
        {
            resources.Add(new XElement(DefaultNs + "Resource", new XAttribute("Language", lang)));
        }
    }

    #endregion

    #region Namespace Management

    /// <summary>
    /// Adds a prefix to the IgnorableNamespaces attribute on the Package element if not already present.
    /// </summary>
    public void AddIgnorableNamespace(string prefix)
    {
        var root = _document.Root;
        if (root == null)
        {
            return;
        }

        var ignorableAttr = root.Attribute("IgnorableNamespaces");
        if (ignorableAttr != null)
        {
            var namespaces = ignorableAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!namespaces.Contains(prefix, StringComparer.OrdinalIgnoreCase))
            {
                ignorableAttr.Value = ignorableAttr.Value + " " + prefix;
            }
        }
        else
        {
            root.SetAttributeValue("IgnorableNamespaces", prefix);
        }
    }

    /// <summary>
    /// Adds an xmlns:prefix declaration to the Package element if not already present.
    /// </summary>
    public void EnsureNamespace(string prefix, XNamespace ns)
    {
        var root = _document.Root;
        if (root == null)
        {
            return;
        }

        var existing = root.Attribute(XNamespace.Xmlns + prefix);
        if (existing == null)
        {
            root.Add(new XAttribute(XNamespace.Xmlns + prefix, ns.NamespaceName));
        }
    }

    #endregion

    #region Capabilities

    /// <summary>
    /// Adds a capability if not already present. Uses the default namespace unless a specific namespace is provided.
    /// </summary>
    public void EnsureCapability(string capabilityName, XNamespace? ns = null)
    {
        var root = _document.Root;
        if (root == null)
        {
            return;
        }

        var capabilities = GetCapabilitiesElement();
        if (capabilities == null)
        {
            capabilities = new XElement(DefaultNs + "Capabilities");
            root.Add(capabilities);
        }

        var targetNs = ns ?? DefaultNs;

        // Check all child elements for a matching Name attribute regardless of namespace
        var existing = capabilities.Elements()
            .FirstOrDefault(e => string.Equals(e.Attribute("Name")?.Value, capabilityName, StringComparison.OrdinalIgnoreCase));

        if (existing == null)
        {
            capabilities.Add(new XElement(targetNs + "Capability", new XAttribute("Name", capabilityName)));
        }
    }

    #endregion

    #region Build Metadata

    /// <summary>
    /// Adds or updates a build:Item entry in the build:Metadata section.
    /// </summary>
    public void SetBuildMetadata(string toolName, string version)
    {
        var root = _document.Root;
        if (root == null)
        {
            return;
        }

        var metadata = root.Element(BuildNs + "Metadata");
        if (metadata == null)
        {
            metadata = new XElement(BuildNs + "Metadata");
            root.Add(metadata);
        }

        var existingItem = metadata.Elements(BuildNs + "Item")
            .FirstOrDefault(e => string.Equals(e.Attribute("Name")?.Value, toolName, StringComparison.OrdinalIgnoreCase));

        if (existingItem != null)
        {
            existingItem.SetAttributeValue("Version", version);
        }
        else
        {
            metadata.Add(new XElement(BuildNs + "Item",
                new XAttribute("Name", toolName),
                new XAttribute("Version", version)));
        }
    }

    #endregion

    #region Package-level Extensions

    /// <summary>
    /// Gets or creates the Package-level Extensions element (direct child of Package root).
    /// This is distinct from Application-level Extensions which live inside Application elements.
    /// </summary>
    public XElement GetOrCreatePackageLevelExtensionsElement()
    {
        var root = _document.Root ?? throw new InvalidOperationException("Document has no root element");

        // root.Element() only returns direct children, so this correctly gets
        // Package-level Extensions (not Application > Extensions)
        var extensions = root.Element(DefaultNs + "Extensions");
        if (extensions != null)
        {
            return extensions;
        }

        extensions = new XElement(DefaultNs + "Extensions");

        // Insert after Applications (standard AppxManifest element order)
        var applications = root.Element(DefaultNs + "Applications");
        if (applications != null)
        {
            applications.AddAfterSelf(extensions);
        }
        else
        {
            root.Add(extensions);
        }

        return extensions;
    }

    /// <summary>
    /// Collects all DLL paths registered in InProcessServer or ProxyStub extensions
    /// (from Package-level <c>&lt;Path&gt;</c> elements). Used for dedup when adding new entries.
    /// </summary>
    public HashSet<string> GetRegisteredExtensionDllPaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = _document.Root;
        if (root == null)
        {
            return result;
        }

        foreach (var path in root.Descendants(DefaultNs + "Path"))
        {
            var text = path.Value?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                result.Add(text);
            }
        }

        return result;
    }

    /// <summary>
    /// Adds an InProcessServer extension entry to the Package-level Extensions element.
    /// </summary>
    public void AddInProcessServerExtension(string dllPath, IEnumerable<string> activatableClasses)
    {
        var extensions = GetOrCreatePackageLevelExtensionsElement();

        var extension = new XElement(DefaultNs + "Extension",
            new XAttribute("Category", "windows.activatableClass.inProcessServer"),
            new XElement(DefaultNs + "InProcessServer",
                new XElement(DefaultNs + "Path", dllPath),
                activatableClasses.Select(cls =>
                    new XElement(DefaultNs + "ActivatableClass",
                        new XAttribute("ActivatableClassId", cls),
                        new XAttribute("ThreadingModel", "both")))));

        extensions.Add(extension);
    }

    #endregion

    #region Package Dependencies

    /// <summary>
    /// Checks if a PackageDependency with the given name prefix exists.
    /// </summary>
    public bool HasPackageDependency(string namePrefix)
    {
        var dependencies = GetDependenciesElement();
        if (dependencies == null)
        {
            return false;
        }

        return dependencies.Elements(DefaultNs + "PackageDependency")
            .Any(e => e.Attribute("Name")?.Value?.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// Adds or updates a PackageDependency element.
    /// </summary>
    public void SetPackageDependency(string name, string minVersion, string publisher)
    {
        var root = _document.Root;
        if (root == null)
        {
            return;
        }

        var dependencies = GetDependenciesElement();
        if (dependencies == null)
        {
            dependencies = new XElement(DefaultNs + "Dependencies");
            root.Add(dependencies);
        }

        var existing = dependencies.Elements(DefaultNs + "PackageDependency")
            .FirstOrDefault(e => string.Equals(e.Attribute("Name")?.Value, name, StringComparison.Ordinal));

        if (existing != null)
        {
            existing.SetAttributeValue("MinVersion", minVersion);
            existing.SetAttributeValue("Publisher", publisher);
        }
        else
        {
            dependencies.Add(new XElement(DefaultNs + "PackageDependency",
                new XAttribute("Name", name),
                new XAttribute("MinVersion", minVersion),
                new XAttribute("Publisher", publisher)));
        }
    }

    #endregion
}
