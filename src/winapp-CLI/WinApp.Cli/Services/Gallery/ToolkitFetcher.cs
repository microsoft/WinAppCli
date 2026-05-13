// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinApp.Cli.Services.Gallery;


internal static partial class ToolkitFetcher
{
    private const string TreeApiUrl =
        "https://api.github.com/repos/CommunityToolkit/Windows/git/trees/main?recursive=1";
    private const string RawBase =
        "https://raw.githubusercontent.com/CommunityToolkit/Windows/main/";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    private static readonly HttpClient Http = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "winui-search/1.0" } },
        Timeout = TimeSpan.FromSeconds(30)
    };

    private const int MaxXamlChars = 1500;
    private const int MaxCSharpChars = 1000;

    /// <summary>Components/samples that aren't visual controls — skip them.</summary>
    private static readonly HashSet<string> SkippedComponents = new(StringComparer.OrdinalIgnoreCase)
    {
        "Triggers", "Converters", "Behaviors", "Extensions", "Helpers",
        "Media", "DeveloperTools", "Animations", "CameraPreview",
        "LayoutTransformControl"
    };

    /// <summary>Sample-file → (controlId, controlName) overrides. Falls back to derived name.</summary>
    private static readonly Dictionary<string, (string id, string name)> SampleOverrides =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // SettingsControls
        ["SettingsCardSample"] = ("settingscard", "SettingsCard"),
        ["ClickableSettingsCardSample"] = ("settingscard", "SettingsCard"),
        ["SettingsExpanderSample"] = ("settingsexpander", "SettingsExpander"),
        ["SettingsExpanderDragHandleSample"] = ("settingsexpander", "SettingsExpander"),
        ["SettingsExpanderItemsSourceSample"] = ("settingsexpander", "SettingsExpander"),
        ["SettingsPageExample"] = ("settingscard", "SettingsCard"),
        // Sizers
        ["ContentSizerLeftShelfPage"] = ("contentsizer", "ContentSizer"),
        ["ContentSizerTopShelfPage"] = ("contentsizer", "ContentSizer"),
        ["GridSplitterPage"] = ("gridsplitter", "GridSplitter"),
        ["PropertySizerNavigationViewPage"] = ("propertysizer", "PropertySizer"),
        ["SizerCursorPage"] = ("contentsizer", "ContentSizer"),
        // Segmented
        ["SegmentedBasicSample"] = ("segmented", "Segmented"),
        ["SegmentedStylesSample"] = ("segmented", "Segmented"),
        ["SegmentedSwitchPresenterSample"] = ("segmented", "Segmented"),
        // HeaderedControls
        ["HeaderedContentControlSample"] = ("headeredcontentcontrol", "HeaderedContentControl"),
        ["HeaderedContentControlComplexSample"] = ("headeredcontentcontrol", "HeaderedContentControl"),
        ["HeaderedContentControlImageSample"] = ("headeredcontentcontrol", "HeaderedContentControl"),
        ["HeaderedContentControlTextSample"] = ("headeredcontentcontrol", "HeaderedContentControl"),
        ["HeaderedItemsControlSample"] = ("headereditemscontrol", "HeaderedItemsControl"),
        ["HeaderedTreeViewSample"] = ("headeredtreeview", "HeaderedTreeView"),
        // Collections
        ["AdvancedCollectionViewSample"] = ("advancedcollectionview", "AdvancedCollectionView"),
        ["IncrementalLoadingCollectionSample"] = ("incrementalloadingcollection", "IncrementalLoadingCollection"),
        // ColorPicker
        ["ColorPickerSample"] = ("colorpicker", "ColorPicker"),
        ["ColorPickerButtonSample"] = ("colorpickerbutton", "ColorPickerButton"),
        // ImageCropper
        ["ImageCropperSample"] = ("imagecropper", "ImageCropper"),
        ["ImageCropperOverlaySample"] = ("imagecropper", "ImageCropper"),
        // RichSuggestBox
        ["RichSuggestBoxPlainText"] = ("richsuggestbox", "RichSuggestBox"),
        ["RichSuggestBoxMultiplePrefixesSample"] = ("richsuggestbox", "RichSuggestBox"),
        // Primitives
        ["DockPanelSample"] = ("dockpanel", "DockPanel"),
        ["StaggeredLayoutSample"] = ("staggeredlayout", "StaggeredLayout"),
        ["StaggeredPanelSample"] = ("staggeredpanel", "StaggeredPanel"),
        ["UniformGridSample"] = ("uniformgrid", "UniformGrid"),
        ["WrapLayoutSample"] = ("wraplayout", "WrapLayout"),
        ["WrapPanelSample"] = ("wrappanel", "WrapPanel"),
    };

    /// <summary>
    /// Default xmlns declarations for visual controls. The "controls" prefix is virtually
    /// always needed; "ui" is added when a sample's XAML actually references it.
    /// </summary>
    private const string XmlnsControls = "xmlns:controls=\"using:CommunityToolkit.WinUI.Controls\"";
    private const string XmlnsUi = "xmlns:ui=\"using:CommunityToolkit.WinUI\"";
    private const string XmlnsAnimations = "xmlns:animations=\"using:CommunityToolkit.WinUI.Animations\"";
    private const string XmlnsBehaviors = "xmlns:behaviors=\"using:CommunityToolkit.WinUI.Behaviors\"";
    private const string XmlnsConverters = "xmlns:converters=\"using:CommunityToolkit.WinUI.Converters\"";

    /// <summary>
    /// Scan a XAML body for which Toolkit-related xmlns prefixes are actually used,
    /// so we only emit the namespaces an agent really needs to add.
    /// </summary>
    private static string[] DetectXmlnsImports(string xaml)
    {
        var imports = new List<string>();
        // controls is always required for any toolkit sample we keep
        imports.Add(XmlnsControls);
        if (UsesPrefix(xaml, "ui"))         imports.Add(XmlnsUi);
        if (UsesPrefix(xaml, "animations")) imports.Add(XmlnsAnimations);
        if (UsesPrefix(xaml, "behaviors"))  imports.Add(XmlnsBehaviors);
        if (UsesPrefix(xaml, "converters")) imports.Add(XmlnsConverters);
        return imports.ToArray();
    }

    private static bool UsesPrefix(string xaml, string prefix)
    {
        // Match either an element start `<prefix:` or a markup extension `{prefix:`
        return Regex.IsMatch(xaml, $@"[<{{]\s*{Regex.Escape(prefix)}:");
    }

    /// <summary>Markdown filename → controlId for tag generation.</summary>
    private static readonly Dictionary<string, string?> MdControlMap =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["SettingsCard.md"] = "settingscard",
        ["SettingsExpander.md"] = "settingsexpander",
        ["ContentSizer.md"] = "contentsizer",
        ["GridSplitter.md"] = "gridsplitter",
        ["PropertySizer.md"] = "propertysizer",
        ["SizerControls.md"] = null,
        ["ConstrainedBox.md"] = "constrainedbox",
        ["DockPanel.md"] = "dockpanel",
        ["StaggeredLayout.md"] = "staggeredlayout",
        ["StaggeredPanel.md"] = "staggeredpanel",
        ["SwitchPresenter.md"] = "switchpresenter",
        ["UniformGrid.md"] = "uniformgrid",
        ["WrapLayout.md"] = "wraplayout",
        ["WrapPanel.md"] = "wrappanel",
        ["AdvancedCollectionView.md"] = "advancedcollectionview",
        ["IncrementalLoadingCollection.md"] = "incrementalloadingcollection",
        ["ColorPicker.md"] = "colorpicker",
        ["HeaderedContentControl.md"] = "headeredcontentcontrol",
        ["HeaderedItemsControl.md"] = "headereditemscontrol",
        ["HeaderedTreeView.md"] = "headeredtreeview",
        ["ImageCropper.md"] = "imagecropper",
        ["MetadataControl.md"] = "metadatacontrol",
        ["RadialGauge.md"] = "radialgauge",
        ["RangeSelector.md"] = "rangeselector",
        ["RichSuggestBox.md"] = "richsuggestbox",
        ["Segmented.md"] = "segmented",
        ["TabbedCommandBar.md"] = "tabbedcommandbar",
        ["TokenizingTextBox.md"] = "tokenizingtextbox",
    };

    // Stop words come from shared StopWords.Common

    /// <summary>Bump when the cached JSON schema changes (e.g., new fields on Scenario).</summary>
    private const string CacheSchemaVersion = "2";

    public static (Scenario[] scenarios, Dictionary<string, string[]> tags) Load(string cacheDir)
    {
        var cacheScenarios = Path.Combine(cacheDir, "scenarios.json");
        var cacheTags = Path.Combine(cacheDir, "tags.json");
        var timestamp = Path.Combine(cacheDir, "last-updated.txt");
        var versionFile = Path.Combine(cacheDir, "schema-version.txt");

        // Cache hit? Check both freshness and schema version.
        if (File.Exists(cacheScenarios) && File.Exists(cacheTags) && File.Exists(timestamp) && File.Exists(versionFile))
        {
            var cachedVersion = File.ReadAllText(versionFile).Trim();
            if (cachedVersion == CacheSchemaVersion
                && DateTime.TryParse(File.ReadAllText(timestamp).Trim(), out var lastUpdated)
                && DateTime.UtcNow - lastUpdated < CacheTtl)
            {
                try
                {
                    var s = JsonSerializer.Deserialize(File.ReadAllText(cacheScenarios), GalleryJsonContext.Default.ScenarioArray);
                    var t = JsonSerializer.Deserialize(File.ReadAllText(cacheTags), GalleryJsonContext.Default.DictionaryStringStringArray);
                    if (s != null && s.Length > 0 && t != null) return (s, t);
                }
                catch { /* fall through */ }
            }
        }

        // Try fetch
        try
        {
            var (scenarios, tags) = FetchFromGitHub().GetAwaiter().GetResult();
            if (scenarios.Length > 0)
            {
                Directory.CreateDirectory(cacheDir);
                File.WriteAllText(cacheScenarios, JsonSerializer.Serialize(scenarios, GalleryJsonContext.Default.ScenarioArray));
                File.WriteAllText(cacheTags, JsonSerializer.Serialize(tags, GalleryJsonContext.Default.DictionaryStringStringArray));
                File.WriteAllText(timestamp, DateTime.UtcNow.ToString("o"));
                File.WriteAllText(versionFile, CacheSchemaVersion);
                return (scenarios, tags);
            }
        }
        catch { /* fall through */ }

        // Embedded fallback
        return (DataLoader.LoadToolkitScenarios(), DataLoader.LoadToolkitTags());
    }

    public static void ClearCache(string cacheDir)
    {
        if (Directory.Exists(cacheDir))
        {
            try { Directory.Delete(cacheDir, true); } catch { }
        }
    }

    private static async Task<(Scenario[], Dictionary<string, string[]>)> FetchFromGitHub()
    {
        // Step 1: Get full file tree
        var treeJson = await Http.GetStringAsync(TreeApiUrl);
        using var doc = JsonDocument.Parse(treeJson);
        var tree = doc.RootElement.GetProperty("tree");

        var xamlSamples = new List<string>();   // paths like components/X/samples/Y.xaml
        var mdDocs = new List<string>();        // paths like components/X/samples/Y.md

        // Auto-discover NuGet package names from components/<Component>/src/<Package>.csproj.
        // The csproj filename equals the canonical NuGet package id for every component.
        var nugetByComponent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in tree.EnumerateArray())
        {
            var path = entry.GetProperty("path").GetString() ?? "";
            if (!path.StartsWith("components/", StringComparison.Ordinal)) continue;
            var parts = path.Split('/');

            // components/<Component>/src/<Package>.csproj  →  package mapping
            if (parts.Length == 4 && parts[2].Equals("src", StringComparison.OrdinalIgnoreCase)
                && parts[3].EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                nugetByComponent[parts[1]] = parts[3][..^".csproj".Length];
                continue;
            }

            // components/<Component>/samples/<File>  →  sample / md
            if (parts.Length != 4 || !parts[2].Equals("samples", StringComparison.OrdinalIgnoreCase)) continue;
            if (SkippedComponents.Contains(parts[1])) continue;

            var file = parts[3];
            if (file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) &&
                !file.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase))
            {
                xamlSamples.Add(path);
            }
            else if (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                mdDocs.Add(path);
            }
        }

        // Step 2: Fetch all sample XAML + C# (parallel) — pass NuGet map down so each
        // FetchSampleAsync can stamp the right package id on its scenarios.
        var sem = new SemaphoreSlim(10);
        var sampleTasks = xamlSamples.Select(p => FetchSampleAsync(p, sem, nugetByComponent)).ToArray();
        var sampleResults = await Task.WhenAll(sampleTasks);
        var allScenarios = sampleResults.Where(s => s != null).SelectMany(s => s!).ToArray();

        // Deduplicate IDs
        var seen = new Dictionary<string, int>();
        foreach (var s in allScenarios)
        {
            if (seen.TryGetValue(s.Id, out var n))
            {
                seen[s.Id] = n + 1;
                s.Id = $"{s.Id}-{n + 1}";
            }
            else
            {
                seen[s.Id] = 1;
            }
        }

        // Step 3: Fetch all md docs + extract tags (parallel)
        var mdTasks = mdDocs.Select(p => FetchMdAsync(p, sem)).ToArray();
        var mdResults = await Task.WhenAll(mdTasks);
        var tags = new Dictionary<string, string[]>();
        foreach (var (cid, tagList) in mdResults.Where(r => r.cid != null))
        {
            tags[cid!] = tagList!;
        }
        // ColorPickerButton has no separate md
        if (tags.ContainsKey("colorpicker") && !tags.ContainsKey("colorpickerbutton"))
        {
            tags["colorpickerbutton"] = ["colorpickerbutton","color","picker","button","dropdown","communitytoolkit"];
        }

        return (allScenarios, tags);
    }

    private static async Task<List<Scenario>?> FetchSampleAsync(
        string path,
        SemaphoreSlim sem,
        Dictionary<string, string> nugetByComponent)
    {
        await sem.WaitAsync();
        try
        {
            var sampleName = Path.GetFileNameWithoutExtension(path);
            var componentName = path.Split('/')[1];

            var (controlId, controlName) = GetControlInfo(componentName, sampleName);

            var xamlText = await TryGetString(RawBase + path);
            if (xamlText == null) return null;

            var csPath = path + ".cs";
            var csText = await TryGetString(RawBase + csPath) ?? "";

            // Strip Page wrapper from XAML
            var pageContent = ExtractPageContent(xamlText);
            var cleanedXaml = CleanXaml(pageContent);

            // Try splitting StackPanel children
            var splits = SplitStackPanelChildren(cleanedXaml, controlName);

            var scenarios = new List<Scenario>();

            // NuGet package id is auto-discovered from the component's src/*.csproj filename.
            // xmlns is auto-detected from which prefixes the cleaned XAML actually references.
            var nuget = nugetByComponent.TryGetValue(componentName, out var pkg)
                ? pkg
                : "CommunityToolkit.WinUI";
            var xmlns = DetectXmlnsImports(cleanedXaml);

            if (splits.Count > 1)
            {
                // Multiple instances → split each into its own scenario
                foreach (var (slugLabel, headerText, xml) in splits)
                {
                    var sid = MakeScenarioId(controlId, slugLabel);
                    scenarios.Add(new Scenario
                    {
                        Id = sid,
                        ControlId = controlId,
                        ControlName = controlName,
                        HeaderText = headerText,
                        Xaml = xml,
                        CSharp = null,
                        Source = "toolkit",
                        NuGetPackage = nuget,
                        XmlnsImports = xmlns,
                    });
                }
            }
            else
            {
                // Single sample
                var friendly = DeriveFriendlyName(sampleName, controlName);
                if (string.IsNullOrEmpty(friendly)) friendly = "Basic usage";
                var sid = MakeScenarioId(controlId, friendly);
                var cs = IsEmptyCodeBehind(csText) ? "" : CleanCSharp(csText, sampleName);

                // Smart truncation: if the cleaned XAML is too big, try to extract just
                // the core <controls:Name> element(s) so we don't lose the actual usage.
                var xamlOut = cleanedXaml;
                if (xamlOut.Length > MaxXamlChars)
                {
                    var focused = ExtractCoreControl(xamlOut, controlName);
                    if (focused != null) xamlOut = focused;
                }
                xamlOut = TruncateXaml(xamlOut, MaxXamlChars);

                scenarios.Add(new Scenario
                {
                    Id = sid,
                    ControlId = controlId,
                    ControlName = controlName,
                    HeaderText = friendly,
                    Xaml = xamlOut,
                    CSharp = string.IsNullOrEmpty(cs) ? null : TruncateCSharp(cs, MaxCSharpChars),
                    Source = "toolkit",
                    NuGetPackage = nuget,
                    XmlnsImports = xmlns,
                });
            }

            return scenarios;
        }
        catch { return null; }
        finally { sem.Release(); }
    }

    private static async Task<(string? cid, string[]? tags)> FetchMdAsync(string path, SemaphoreSlim sem)
    {
        await sem.WaitAsync();
        try
        {
            var fileName = Path.GetFileName(path);
            if (!MdControlMap.TryGetValue(fileName, out var controlId) || controlId == null)
                return (null, null);

            var text = await TryGetString(RawBase + path);
            if (text == null) return (null, null);

            return (controlId, ExtractTags(text, controlId));
        }
        catch { return (null, null); }
        finally { sem.Release(); }
    }

    private static async Task<string?> TryGetString(string url)
    {
        try
        {
            var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync();
        }
        catch { return null; }
    }

    private static (string id, string name) GetControlInfo(string componentName, string sampleName)
    {
        if (SampleOverrides.TryGetValue(sampleName, out var hit)) return hit;
        // Default: derive from component name
        return (componentName.ToLowerInvariant(), componentName);
    }

    [GeneratedRegex(@"<Page\b[^>]*>(.*)</Page>", RegexOptions.Singleline)]
    private static partial Regex PageRegex();

    private static string ExtractPageContent(string xaml)
    {
        // Remove license comment
        xaml = Regex.Replace(xaml, @"<!--\s*Licensed to.*?-->\s*", "", RegexOptions.Singleline);
        var m = PageRegex().Match(xaml);
        return m.Success ? m.Groups[1].Value.Trim() : xaml;
    }

    private static string CleanXaml(string xaml)
    {
        // Demo bindings + names
        xaml = Regex.Replace(xaml, @"\s+IsEnabled=""\{x:Bind\s+\w+,\s*Mode=OneWay\}""", "");
        xaml = Regex.Replace(xaml, @"\s+IsExpanded=""\{x:Bind\s+\w+,\s*Mode=OneWay\}""", "");
        xaml = Regex.Replace(xaml, @"\s+x:Name=""\w+""", "");
        xaml = Regex.Replace(xaml, @"\s+AutomationProperties\.\w+=""[^""]*""", "");
        xaml = Regex.Replace(xaml, @"<!--\s*TODO:.*?-->", "", RegexOptions.Singleline);
        xaml = Regex.Replace(xaml, @"<!--.*?-->", "", RegexOptions.Singleline);
        // Demo sizes
        xaml = Regex.Replace(xaml, @"\s+(?:Min|Max)?Height=""\d+""", "");
        xaml = Regex.Replace(xaml, @"\s+(?:Min|Max)?Width=""\d+""", "");
        // Demo positioning / spacing on demo containers
        xaml = Regex.Replace(xaml, @"\s+Padding=""\d+(?:,\d+)*""", "");
        xaml = Regex.Replace(xaml, @"\s+Margin=""[\d,\-]+""", "");
        xaml = Regex.Replace(xaml, @"\s+Spacing=""\d+""", "");
        // Demo defaults
        xaml = Regex.Replace(xaml, @"\s+SelectedIndex=""\d+""", "");
        xaml = Regex.Replace(xaml, @"\s+(?:Horizontal|Vertical)Alignment=""(?:Stretch|Center|Top|Left)""", "");
        // Theme/system resource references — keep useful ones, drop generic CardStyle etc.
        xaml = Regex.Replace(xaml, @"\s+Background=""\{ThemeResource\s+\w+\}""", "");
        xaml = Regex.Replace(xaml, @"\s+BorderBrush=""\{ThemeResource\s+\w+\}""", "");
        xaml = Regex.Replace(xaml, @"\s+BorderThickness=""[\d,]+""", "");
        xaml = Regex.Replace(xaml, @"\s+CornerRadius=""\d+""", "");
        xaml = Regex.Replace(xaml, @"\s+Style=""\{StaticResource\s+(?:CardStyle|EmployeeDataTemplate|EmailTemplate|StaggeredTemplate|PhotoTemplate|SuggestionTemplate)\}""", "");
        // Drop Page.Resources / Style / VisualStateManager / Resources blocks
        xaml = Regex.Replace(xaml, @"<Page\.Resources>.*?</Page\.Resources>\s*", "", RegexOptions.Singleline);
        xaml = Regex.Replace(xaml, @"<\w+\.Resources>.*?</\w+\.Resources>\s*", "", RegexOptions.Singleline);
        xaml = Regex.Replace(xaml, @"<VisualStateManager\.VisualStateGroups>.*?</VisualStateManager\.VisualStateGroups>\s*", "", RegexOptions.Singleline);
        // Drop standalone descriptive TextBlocks (Text > 60 chars, only Text attribute, no x:Bind)
        xaml = Regex.Replace(
            xaml,
            @"<TextBlock\s+Text=""[^""]{60,}""\s*(?:TextWrapping=""[^""]*""\s*)?/>\s*",
            "",
            RegexOptions.Singleline);
        // Drop TextBlock with only Run children (descriptive Run blocks)
        xaml = Regex.Replace(
            xaml,
            @"<TextBlock\s*>\s*(?:<Run\b[^>]*?(?:>[^<]*</Run>|/>)\s*){1,}</TextBlock>\s*",
            "",
            RegexOptions.Singleline);
        // Drop empty Border placeholders
        xaml = Regex.Replace(xaml, @"<Border[^/>]*?/>\s*", "");
        xaml = Regex.Replace(xaml, @"<Border[^>]*?>\s*</Border>\s*", "");
        // Drop empty Grid cells used as demo placeholders
        xaml = Regex.Replace(xaml, @"<Grid\s+Grid\.(?:Row|Column)=""\d+""[^>]*?>\s*</Grid>\s*", "");
        // Compress whitespace
        var lines = xaml.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l));
        return string.Join('\n', lines);
    }

    /// <summary>
    /// When a sample is too large, try to extract just the core control element(s)
    /// (e.g., for a GridSplitterSample, find &lt;controls:GridSplitter&gt; blocks).
    /// Returns the focused subset if found and shorter; otherwise returns null.
    /// </summary>
    private static string? ExtractCoreControl(string xaml, string controlName)
    {
        var name = Regex.Escape(controlName);
        // Match self-closing or paired controls:Name elements
        var selfClose = $@"<controls:{name}\b[^>]*/>";
        var paired = $@"<controls:{name}\b[^>]*?>[\s\S]*?</controls:{name}>";
        var matches = Regex.Matches(xaml, $@"({selfClose}|{paired})", RegexOptions.Singleline);
        if (matches.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        foreach (Match m in matches)
        {
            sb.AppendLine(m.Value.Trim());
            sb.AppendLine();
        }
        var focused = sb.ToString().TrimEnd();
        // Only use if it actually saves space (otherwise full context is better)
        if (focused.Length < xaml.Length * 0.8) return focused;
        return null;
    }

    private static List<(string slugKey, string headerText, string xaml)> SplitStackPanelChildren(string xaml, string targetControl)
    {
        var trimmed = xaml.TrimStart();
        if (!Regex.IsMatch(trimmed, @"^<StackPanel[\s>]")) return new();

        var name = Regex.Escape(targetControl);
        var pattern = $@"(<controls:{name}\b[^>]*/>|<controls:{name}\b[^>]*?>.*?</controls:{name}>)";
        var matches = Regex.Matches(xaml, pattern, RegexOptions.Singleline);
        if (matches.Count <= 1) return new();

        var results = new List<(string, string, string)>();
        int i = 0;
        foreach (Match m in matches)
        {
            i++;
            var block = m.Groups[1].Value;
            var open = Regex.Match(block, $@"<controls:{name}\b(.*?)(?:/>|>)", RegexOptions.Singleline);
            string? rawHeader = null;
            string? rawDescription = null;
            if (open.Success)
            {
                var attrs = open.Groups[1].Value;
                var hm = Regex.Match(attrs, @"\bHeader=""([^""]*)""");
                var dm = Regex.Match(attrs, @"\bDescription=""([^""]*)""");
                if (hm.Success) rawHeader = hm.Groups[1].Value.Trim();
                if (dm.Success) rawDescription = dm.Groups[1].Value.Trim();
            }
            // Pick a SHORT label for the URL slug (max 6 words from header or description).
            string slugLabel = PickSlugLabel(rawHeader, rawDescription, targetControl, i);
            // Build the full description shown in search results.
            string fullDesc = BuildScenarioDescription(rawHeader, rawDescription, targetControl, i);
            results.Add((slugLabel, fullDesc, block.Trim()));
        }
        return results;
    }

    /// <summary>
    /// Pick the short label used to build the URL slug. Avoid slugs longer than ~6 words.
    /// </summary>
    private static string PickSlugLabel(string? header, string? description, string controlName, int index)
    {
        // Prefer header if it's not a placeholder
        if (!IsPlaceholderHeader(header, controlName))
            return TruncateWords(header!, 6);
        // Otherwise use the first few words of the description
        if (!string.IsNullOrWhiteSpace(description) && !IsPlaceholderHeader(description, controlName))
            return TruncateWords(description!, 6);
        // Last resort: index
        return $"example-{index}";
    }

    private static string TruncateWords(string text, int maxWords)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= maxWords) return text;
        return string.Join(' ', words.Take(maxWords));
    }

    /// <summary>
    /// Combine raw Header / Description attributes into a useful agent-facing description.
    /// Falls back to control-name-based defaults for placeholder text like "This is the Header"
    /// or descriptions that just repeat the control name.
    /// </summary>
    private static string BuildScenarioDescription(string? header, string? description, string controlName, int index)
    {
        var label = IsPlaceholderHeader(header, controlName) ? null : header;
        string? desc = description?.Trim();
        if (!string.IsNullOrWhiteSpace(desc) && desc!.Length > 120)
        {
            int dot = desc.IndexOf('.', 60);
            desc = (dot > 0 && dot < 120) ? desc.Substring(0, dot + 1) : desc.Substring(0, 117) + "...";
        }

        if (!string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(desc) && !desc!.Equals(label, StringComparison.OrdinalIgnoreCase))
            return $"{label} — {desc}";
        if (!string.IsNullOrEmpty(label)) return label!;
        if (!string.IsNullOrEmpty(desc)) return desc!;
        return $"Example {index}";
    }

    /// <summary>
    /// Return true if a Header attribute is a generic demo placeholder (not informative).
    /// </summary>
    private static bool IsPlaceholderHeader(string? header, string controlName)
    {
        if (string.IsNullOrWhiteSpace(header)) return true;
        var h = header.Trim();
        var hNorm = Regex.Replace(h.ToLowerInvariant(), @"[^a-z0-9]", "");
        var cNorm = Regex.Replace(controlName.ToLowerInvariant(), @"[^a-z0-9]", "");
        // Identical / "X control" / "X sample"
        if (hNorm == cNorm) return true;
        if (hNorm == cNorm + "control" || hNorm == cNorm + "sample") return true;
        // Generic single-word "Header" or "Sample"
        if (h.Equals("Header", StringComparison.OrdinalIgnoreCase)) return true;
        // "Example N" / "Sample N"
        if (Regex.IsMatch(h, @"^Example\s*\d*$", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(h, @"^Sample\s*\d*$", RegexOptions.IgnoreCase)) return true;
        // Specific demo placeholders ("This is the Header", "This is a Header", etc.)
        // — keep narrow so real descriptions starting with "This is..." aren't dropped.
        if (Regex.IsMatch(h, @"^This is (the|a|an) (Header|Description|Sample|Example|Control|Card)\b", RegexOptions.IgnoreCase)) return true;
        return false;
    }

    private static string DeriveFriendlyName(string sampleName, string controlName)
    {
        var name = sampleName;
        if (name.EndsWith("Sample", StringComparison.Ordinal)) name = name[..^"Sample".Length];
        if (name.EndsWith("Page", StringComparison.Ordinal)) name = name[..^"Page".Length];
        // Strip control name prefix BEFORE spacing, so "ColorPickerSample" → "ColorPicker" → ""
        var bare = controlName.Replace(" ", "");
        if (name.StartsWith(bare, StringComparison.OrdinalIgnoreCase))
            name = name[bare.Length..];
        // If nothing distinctive remains, call it "Basic usage" (don't echo the control name).
        if (string.IsNullOrEmpty(name)) return "Basic usage";
        // Insert spaces before capitals for readability
        name = Regex.Replace(name, @"(?<=[a-z])(?=[A-Z])", " ");
        return name.Trim();
    }

    private static bool IsEmptyCodeBehind(string cs)
    {
        var stripped = cs.Trim();
        if (string.IsNullOrEmpty(stripped)) return true;
        var s = Regex.Replace(stripped, @"namespace\s+\S+;?\s*", "");
        s = Regex.Replace(s, @"(?:public\s+)?sealed\s+partial\s+class\s+\w+\s*:\s*Page\s*\{", "");
        s = Regex.Replace(s, @"public\s+\w+\(\)\s*\{\s*this\.InitializeComponent\(\);\s*\}", "");
        s = Regex.Replace(s, @"[{}]", "").Trim();
        return s.Length < 20;
    }

    private static string CleanCSharp(string cs, string sampleName)
    {
        // License header
        cs = Regex.Replace(cs, @"^//\s*Licensed to.*?(?=\n[^/])", "", RegexOptions.Singleline);
        cs = Regex.Replace(cs, @"^//\s*The \.NET Foundation.*?\n", "", RegexOptions.Multiline);
        cs = Regex.Replace(cs, @"^//\s*See the LICENSE.*?\n", "", RegexOptions.Multiline);
        // [ToolkitSample(...)] attributes
        cs = Regex.Replace(cs, @"\[ToolkitSample[^\]]*\][\r\n]*", "", RegexOptions.Singleline);
        cs = Regex.Replace(cs, @"\[ToolkitSampleOptionsPane[^\]]*\][\r\n]*", "", RegexOptions.Singleline);
        cs = Regex.Replace(cs, @"\[SuppressMessage[^\]]*\][\r\n]*", "", RegexOptions.Singleline);
        // Original namespace declaration → replace with placeholder so the class wrapper compiles
        cs = Regex.Replace(cs, @"namespace\s+[\w.]+\s*(?:;|\{)\s*", "namespace YourApp;\n\n");
        // Rename the sample class to a clearer placeholder name so agents know to rename
        if (!string.IsNullOrEmpty(sampleName))
        {
            cs = Regex.Replace(cs,
                $@"\b{Regex.Escape(sampleName)}\b",
                "YourPage");
        }
        // Drop trailing blank lines
        cs = Regex.Replace(cs, @"\n\s*\n\s*\n+", "\n\n");
        return cs.Trim();
    }

    [GeneratedRegex(@"<(/?)([A-Za-z_][\w:.\-]*)\b([^>]*?)(/?)>")]
    private static partial Regex AnyTagRegex();

    private static string TruncateXaml(string xaml, int maxChars)
    {
        bool needsTrunc = xaml.Length > maxChars;
        string head;
        if (needsTrunc)
        {
            int cut = maxChars;
            while (cut > 0)
            {
                cut = xaml.LastIndexOf('>', cut - 1);
                if (cut < 0) return "";
                cut += 1;
                int lastLt = xaml.LastIndexOf('<', cut - 1);
                int lastGt = xaml.LastIndexOf('>', cut - 1);
                if (lastLt < lastGt) break;
                cut = lastLt;
            }
            if (cut <= 0) return "";
            head = xaml.Substring(0, cut);
        }
        else
        {
            head = xaml;
        }

        var stack = new Stack<string>();
        bool sawMismatch = false;
        foreach (Match m in AnyTagRegex().Matches(head))
        {
            bool isClose = m.Groups[1].Value == "/";
            bool isSelf = m.Groups[4].Value == "/";
            string nm = m.Groups[2].Value;
            if (isSelf) continue;
            if (isClose)
            {
                if (stack.Count > 0 && stack.Peek() == nm) stack.Pop();
                else sawMismatch = true;
            }
            else
            {
                stack.Push(nm);
            }
        }

        if (!needsTrunc && stack.Count == 0 && !sawMismatch) return xaml;

        var sb = new System.Text.StringBuilder(head.TrimEnd());
        while (stack.Count > 0) sb.Append("</").Append(stack.Pop()).Append('>');
        if (needsTrunc) sb.Append("\n<!-- NOTE: XAML truncated — additional sibling elements omitted -->");
        return sb.ToString();
    }

    private static string TruncateCSharp(string code, int maxChars)
    {
        if (code.Length <= maxChars) return code;
        int cut = code.LastIndexOf('\n', maxChars - 1);
        if (cut < 0) cut = maxChars;
        return code.Substring(0, cut).TrimEnd() + "\n// NOTE: snippet truncated — refer to full sample for additional code";
    }

    private static string MakeScenarioId(string controlId, string header)
    {
        var slug = Regex.Replace(header.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 50) slug = slug.Substring(0, 50).TrimEnd('-');
        return $"{controlId}-{slug}";
    }

    // ─── Markdown frontmatter / tag extraction ─────────────────────────

    private static string[] ExtractTags(string mdText, string controlId)
    {
        var fm = ParseFrontmatter(mdText);
        var tags = new List<string> { controlId };

        // Title → split CamelCase
        if (fm.TryGetValue("title", out var title))
        {
            foreach (Match m in Regex.Matches(title, @"[A-Z]?[a-z]+|[A-Z]+"))
            {
                var p = m.Value.ToLowerInvariant();
                if (p.Length >= 3 && !StopWords.Common.Contains(p)) tags.Add(p);
            }
        }
        // keywords (comma-separated)
        if (fm.TryGetValue("keywords", out var keywords))
        {
            foreach (var kw in keywords.Split(','))
            {
                var t = kw.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(t) && !StopWords.Common.Contains(t)) tags.Add(t);
            }
        }
        // subcategory
        if (fm.TryGetValue("subcategory", out var sub))
        {
            var s = sub.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(s) && !StopWords.Common.Contains(s)) tags.Add(s);
        }
        // Description text keywords
        var descText = Regex.Replace(mdText, @"^---.*?---\s*", "", RegexOptions.Singleline);
        var paras = descText.Trim().Split("\n\n").Take(2);
        var desc = string.Join(' ', paras);
        desc = Regex.Replace(desc, @"\[([^\]]+)\]\([^)]+\)", "$1");   // links
        desc = Regex.Replace(desc, @"`[^`]+`", "");                    // inline code
        desc = Regex.Replace(desc, @"[>#*_\[\]]", "");
        var words = Regex.Matches(desc.ToLowerInvariant(), @"[a-z]{4,}");
        var seen = new HashSet<string>(tags);
        int descAdded = 0;
        foreach (Match m in words)
        {
            if (descAdded >= 8) break;
            var w = m.Value;
            if (StopWords.Common.Contains(w) || !seen.Add(w)) continue;
            tags.Add(w);
            descAdded++;
        }
        tags.Add("communitytoolkit");
        // Final dedup
        var final = new List<string>();
        var fseen = new HashSet<string>();
        foreach (var t in tags)
        {
            if (fseen.Add(t)) final.Add(t);
        }
        return final.ToArray();
    }

    private static Dictionary<string, string> ParseFrontmatter(string md)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var m = Regex.Match(md, @"^---\s*\n(.*?)\n---", RegexOptions.Singleline);
        if (!m.Success) return result;
        foreach (var line in m.Groups[1].Value.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('-')) continue;
            int colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;
            var key = trimmed.Substring(0, colon).Trim();
            var val = trimmed.Substring(colon + 1).Trim();
            result[key] = val;
        }
        return result;
    }
}


