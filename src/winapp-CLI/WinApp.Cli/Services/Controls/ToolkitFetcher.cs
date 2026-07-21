// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static partial class ToolkitFetcher
{
    private const string TreeApiUrl =
        "https://api.github.com/repos/CommunityToolkit/Windows/git/trees/main?recursive=1";
    private const string RawBase =
        "https://raw.githubusercontent.com/CommunityToolkit/Windows/main/";

    private static readonly HttpClient Http = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "winui-search/1.0" } },
        Timeout = TimeSpan.FromSeconds(30)
    };

    private const int MaxXamlChars = 1000;
    private const int MaxCSharpChars = 2500;

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

    /// <summary>Fetch fresh scenarios, tags, and keywords from GitHub. Tags are
    /// stop-word-cleaned before return.</summary>
    internal static async Task<(Scenario[] scenarios, Dictionary<string, string[]> tags, Dictionary<string, string[]> keywords)> FetchAsync(CancellationToken cancellationToken = default)
    {
        var (scenarios, tags, keywords) = await FetchFromGitHub(cancellationToken);
        if (scenarios.Length > 0) tags = StopWords.CleanTagDictionary(tags);
        return (scenarios, tags, keywords);
    }

    private static async Task<(Scenario[], Dictionary<string, string[]>, Dictionary<string, string[]>)> FetchFromGitHub(CancellationToken cancellationToken)
    {
        // Step 1: Get full file tree
        var treeJson = await ControlsHttpHelper.GetStringCappedAsync(Http, TreeApiUrl, cancellationToken);
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

        // Sort sample / md paths so downstream processing (and the per-control
        // renumbering below) is deterministic regardless of GitHub tree ordering.
        xamlSamples.Sort(StringComparer.OrdinalIgnoreCase);
        mdDocs.Sort(StringComparer.OrdinalIgnoreCase);

        // Step 2: Fetch all md docs FIRST (parallel) — gives us tags + descriptions
        // before processing samples, so each scenario can be enriched with prose.
        using var sem = new SemaphoreSlim(10);
        var mdTasks = mdDocs.Select(p => FetchMdAsync(p, sem, cancellationToken)).ToArray();
        var mdResults = await Task.WhenAll(mdTasks);
        var tags = new Dictionary<string, string[]>();
        var keywords = new Dictionary<string, string[]>();
        var controlDescByCid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sampleDescByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var md in mdResults)
        {
            if (md.ControlId != null && md.Tags != null) tags[md.ControlId] = md.Tags;
            if (md.ControlId != null && md.Keywords != null && md.Keywords.Length > 0)
                keywords[md.ControlId] = md.Keywords;
            if (md.ControlId != null && md.ControlDescription != null)
                controlDescByCid[md.ControlId] = md.ControlDescription;
            if (md.SampleDescriptions != null)
            {
                foreach (var (k, v) in md.SampleDescriptions) sampleDescByName[k] = v;
            }
        }
        // ColorPickerButton has no separate md
        if (tags.ContainsKey("colorpicker") && !tags.ContainsKey("colorpickerbutton"))
        {
            tags["colorpickerbutton"] = ["colorpickerbutton","color","picker","button","dropdown","communitytoolkit"];
        }
        if (keywords.ContainsKey("colorpicker") && !keywords.ContainsKey("colorpickerbutton"))
        {
            keywords["colorpickerbutton"] = keywords["colorpicker"];
        }
        if (controlDescByCid.TryGetValue("colorpicker", out var cpDesc) && !controlDescByCid.ContainsKey("colorpickerbutton"))
        {
            controlDescByCid["colorpickerbutton"] = cpDesc;
        }

        // Step 3: Fetch all sample XAML + C# (parallel) — pass NuGet map and description
        // maps down so each FetchSampleAsync can stamp packaging + prose on its scenarios.
        var sampleTasks = xamlSamples.Select(p => FetchSampleAsync(p, sem, nugetByComponent, controlDescByCid, sampleDescByName, cancellationToken)).ToArray();
        var sampleResults = await Task.WhenAll(sampleTasks);
        var allScenarios = sampleResults.Where(s => s != null).SelectMany(s => s!).ToArray();

        // Renumber scenario IDs per controlId to {controlId}-{N} (1-indexed) for
        // a uniform short ID scheme across Gallery and Toolkit. Order follows the
        // sample-path sort above (Task.WhenAll + SelectMany + GroupBy all preserve
        // input order), so {controlId}-{N} is stable across fetches as long as
        // the underlying sample file paths don't change.
        var byControl = allScenarios
            .GroupBy(s => s.ControlId, StringComparer.OrdinalIgnoreCase);
        foreach (var grp in byControl)
        {
            var idx = 0;
            foreach (var s in grp)
            {
                idx++;
                s.Id = $"{grp.Key}-{idx}";
            }
        }

        return (allScenarios, tags, keywords);
    }

    private static async Task<List<Scenario>?> FetchSampleAsync(
        string path,
        SemaphoreSlim sem,
        Dictionary<string, string> nugetByComponent,
        Dictionary<string, string> controlDescByCid,
        Dictionary<string, string> sampleDescByName,
        CancellationToken cancellationToken)
    {
        await sem.WaitAsync(cancellationToken);
        try
        {
            var sampleName = Path.GetFileNameWithoutExtension(path);
            var componentName = path.Split('/')[1];

            var (controlId, controlName) = GetControlInfo(componentName, sampleName);

            var xamlText = await TryGetString(RawBase + path, cancellationToken);
            if (xamlText == null) return null;

            var csPath = path + ".cs";
            var csText = await TryGetString(RawBase + csPath, cancellationToken) ?? "";

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

            // Look up descriptions: prefer the per-sample paragraph from the .md (most specific),
            // fall back to the control-level frontmatter description.
            string? controlDesc = controlDescByCid.TryGetValue(controlId, out var cd) ? cd : null;
            string? sampleDesc = sampleDescByName.TryGetValue(sampleName, out var sd) ? sd : null;

            if (splits.Count > 1)
            {
                // Multiple instances → split each into its own scenario.
                // All splits come from the same sample file, so they share its description
                // (from the .md preceding [!SAMPLE PageName]). Fall back to the per-sample
                // XAML <Sample Description="..."> attribute when no .md paragraph is present;
                // this avoids the previous behaviour of jamming Header + Description into
                // one string and then ALSO appending the .md paragraph (visible duplication).
                foreach (var (slugLabel, label, xamlDesc, xml) in splits)
                {
                    var sid = MakeScenarioId(controlId, slugLabel);
                    scenarios.Add(new Scenario
                    {
                        Id = sid,
                        ControlId = controlId,
                        ControlName = controlName,
                        HeaderText = label,
                        Xaml = xml,
                        CSharp = null,
                        Source = "toolkit",
                        NuGetPackage = nuget,
                        XmlnsImports = xmlns,
                        Description = sampleDesc ?? xamlDesc,
                        ControlDescription = controlDesc,
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
                    Description = sampleDesc,
                    ControlDescription = controlDesc,
                });
            }

            return scenarios;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally { sem.Release(); }
    }

    /// <summary>Per-.md aggregated extract: tags (mapped only) + descriptions (frontmatter + per-sample).</summary>
    private sealed record MdData(
        string? ControlId,
        string[]? Tags,
        string[]? Keywords,
        string? ControlDescription,
        Dictionary<string, string>? SampleDescriptions);

    private static async Task<MdData> FetchMdAsync(string path, SemaphoreSlim sem, CancellationToken cancellationToken)
    {
        await sem.WaitAsync(cancellationToken);
        try
        {
            var fileName = Path.GetFileName(path);
            var text = await TryGetString(RawBase + path, cancellationToken);
            if (text == null) return new MdData(null, null, null, null, null);

            // Normalize newlines once: downstream parsers (ParseFrontmatter,
            // ExtractSampleDescriptions, ExtractTags) split on "\n" / "\n\n",
            // which breaks on CRLF. Toolkit repo is LF today but raw bytes
            // could change; fix at the source.
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');

            // Frontmatter description (control-level intro, e.g. "The ContentSizer is a control...")
            var fm = ParseFrontmatter(text);
            string? controlDesc = null;
            if (fm.TryGetValue("description", out var d) && !string.IsNullOrWhiteSpace(d))
                controlDesc = CleanProse(d);

            // Per-sample description: paragraph immediately preceding each [!SAMPLE PageName] marker
            var sampleDescs = ExtractSampleDescriptions(text);

            // Tags + curated keywords only for .md files explicitly mapped to a controlId
            string? cid = null;
            string[]? tags = null;
            string[]? keywords = null;
            if (MdControlMap.TryGetValue(fileName, out var mapped) && mapped != null)
            {
                cid = mapped;
                tags = ExtractTags(text, mapped);
                keywords = ExtractKeywords(text);
            }

            return new MdData(cid, tags, keywords, controlDesc, sampleDescs);
        }
        catch (OperationCanceledException) { throw; }
        catch { return new MdData(null, null, null, null, null); }
        finally { sem.Release(); }
    }

    /// <summary>Parse `> [!SAMPLE PageName]` markers and grab the text between this marker
    /// and the previous SAMPLE / section header / doc start (whichever is closest), filtered
    /// to non-header / non-blockquote prose. Section-header boundaries keep multi-section
    /// overview .md files (like SizerControls.md) from polluting per-sample descriptions.</summary>
    private static Dictionary<string, string> ExtractSampleDescriptions(string md)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Strip frontmatter
        var body = Regex.Replace(md, @"^---\s*\n.*?\n---\s*", "", RegexOptions.Singleline);
        var matches = Regex.Matches(body, @">\s*\[!SAMPLE\s+(\w+)\]", RegexOptions.IgnoreCase);
        int lastEnd = 0;
        foreach (Match m in matches)
        {
            var name = m.Groups[1].Value;
            var preceding = body.Substring(lastEnd, m.Index - lastEnd);
            // Take paragraphs since most recent section header (## or deeper) — keeps
            // descriptions scoped to the local section in multi-section overview docs.
            var paras = preceding.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();
            int sectionStart = 0;
            for (int i = paras.Count - 1; i >= 0; i--)
            {
                if (paras[i].StartsWith("#")) { sectionStart = i + 1; break; }
            }
            var local = paras.Skip(sectionStart)
                .Where(p => !p.StartsWith("#") && !p.StartsWith(">"))  // strip headers / blockquotes
                .ToArray();
            if (local.Length > 0)
            {
                var clean = CleanProse(string.Join(" ", local));
                if (clean.Length > 10) result[name] = clean;
            }
            lastEnd = m.Index + m.Length;
        }
        return result;
    }

    /// <summary>Strip markdown noise (links, inline code) and collapse whitespace; cap at ~240 chars.</summary>
    private static string CleanProse(string text)
    {
        var s = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");  // [text](link) → text
        s = Regex.Replace(s, @"`([^`]+)`", "$1");                      // `code` → code
        s = Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length > 240)
        {
            int dot = s.IndexOf('.', 100);
            s = (dot > 0 && dot < 240) ? s.Substring(0, dot + 1) : s.Substring(0, 237) + "...";
        }
        return s;
    }

    private static async Task<string?> TryGetString(string url, CancellationToken cancellationToken)
    {
        return await ControlsHttpHelper.TryGetStringCappedAsync(Http, url, cancellationToken);
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
        xaml = Regex.Replace(xaml, @"\s+AutomationProperties\.AutomationId=""[^""]*""", "");
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

    private static List<(string slugKey, string label, string? xamlDescription, string xaml)> SplitStackPanelChildren(string xaml, string targetControl)
    {
        var trimmed = xaml.TrimStart();
        if (!Regex.IsMatch(trimmed, @"^<StackPanel[\s>]")) return new();

        var name = Regex.Escape(targetControl);
        var pattern = $@"(<controls:{name}\b[^>]*/>|<controls:{name}\b[^>]*?>.*?</controls:{name}>)";
        var matches = Regex.Matches(xaml, pattern, RegexOptions.Singleline);
        if (matches.Count <= 1) return new();

        var results = new List<(string, string, string?, string)>();
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
            // Header label: short, suitable as the row's "title" — falls back to description
            // or "Example N" only when Header is missing/placeholder.
            string label = BuildScenarioLabel(rawHeader, rawDescription, targetControl, i);
            // XAML Description attribute kept separately so callers can use it as a
            // fallback when the .md doesn't have a per-sample paragraph; truncated to
            // ~120 chars for display.
            string? xamlDesc = TrimDescription(rawDescription);
            results.Add((slugLabel, label, xamlDesc, block.Trim()));
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
    /// Pick a SHORT label for the row (typically the Sample's Header attribute).
    /// Falls back to a truncated Description / "Example N" when Header is a
    /// generic placeholder. The XAML Description attribute is NOT concatenated
    /// here — callers keep it separately so it can be presented (or replaced
    /// by a richer .md paragraph) without duplicating the label.
    /// </summary>
    private static string BuildScenarioLabel(string? header, string? description, string controlName, int index)
    {
        if (!IsPlaceholderHeader(header, controlName)) return header!;
        var desc = TrimDescription(description);
        if (!string.IsNullOrEmpty(desc)) return desc!;
        return $"Example {index}";
    }

    /// <summary>Cap a Description attribute at the first sentence under ~120 chars.</summary>
    private static string? TrimDescription(string? description)
    {
        var d = description?.Trim();
        if (string.IsNullOrWhiteSpace(d)) return null;
        if (d!.Length <= 120) return d;
        int dot = d.IndexOf('.', 60);
        return (dot > 0 && dot < 120) ? d.Substring(0, dot + 1) : d.Substring(0, 117) + "...";
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
        // Insert spaces before uppercase letters for readability
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
        // Fold platform #if/#else/#endif: agents target WinAppSDK, so keep only the
        // WINAPPSDK branch and discard UWP/Uno fallbacks. Done before the rest of the
        // cleanup so we don't waste work on text that's about to be stripped.
        cs = FoldPreprocessorDirectives(cs);
        // [ToolkitSample(...)] and related docs-build attributes (single-line + multi-line, balanced brackets)
        cs = Regex.Replace(cs, @"\[Toolkit(?:Sample|SampleOptionsPane|SampleMultiChoiceOption|SampleNumericOption|SampleBoolOption|SampleTextOption)\b[^\]]*\][\r\n]*", "", RegexOptions.Singleline);
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
        // Drop docs-only converter helpers (back the [ToolkitSample*Option] attributes we just stripped).
        // Pattern: `public static T ConvertStringTo<X>(string ...) => ... switch { ... };` — single statement
        // followed by a switch expression body. These only exist to wire up the docs option pane.
        cs = Regex.Replace(cs,
            @"public\s+static\s+[\w?<>]+\s+ConvertString\w+\s*\([^)]*\)\s*=>\s*\w+\s+switch\s*\{[^}]*\}\s*;",
            "",
            RegexOptions.Singleline);
        // Drop trailing blank lines
        cs = Regex.Replace(cs, @"\n\s*\n\s*\n+", "\n\n");
        return cs.Trim();
    }

    /// <summary>
    /// Compile-time preprocessor folding for toolkit samples. Agents target WinAppSDK,
    /// so we evaluate <c>#if WINAPPSDK</c> as true (and <c>HAS_UNO</c> / <c>WINUI2</c> /
    /// <c>UWP</c> / <c>NETFX_CORE</c> as false), keep the live branch's lines, and drop
    /// the directives + dead branches. Unknown symbols are treated as true (conservative:
    /// keep code rather than silently delete it). Supports <c>#if</c>, <c>#elif</c>,
    /// <c>#else</c>, <c>#endif</c>, single <c>!</c> negation, and nested blocks.
    /// </summary>
    private static string FoldPreprocessorDirectives(string cs)
    {
        if (cs.IndexOf("#if", StringComparison.Ordinal) < 0) return cs;

        // Treat WinAppSDK-targeting symbols as true; UWP/Uno/legacy as false.
        // Anything else: true (preserve code we don't recognize).
        static bool Eval(string expr)
        {
            expr = expr.Trim();
            bool negate = false;
            if (expr.StartsWith('!'))
            {
                negate = true;
                expr = expr[1..].Trim();
            }
            bool value = expr switch
            {
                "WINAPPSDK" or "WINUI3" or "NET" => true,
                "HAS_UNO" or "WINUI2" or "UWP" or "NETFX_CORE" => false,
                _ => true,
            };
            return negate ? !value : value;
        }

        var lines = cs.Replace("\r\n", "\n").Split('\n');
        var output = new List<string>(lines.Length);
        // Stack frame: (anyBranchTakenYet, currentlyEmittingThisBranch).
        // Parent's emitting state is tracked separately via parentEmit.
        var stack = new Stack<(bool taken, bool emit)>();

        bool ParentEmitting()
        {
            foreach (var f in stack)
                if (!f.emit) return false;
            return true;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine;
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("#if ", StringComparison.Ordinal) || trimmed == "#if")
            {
                var expr = trimmed.Length > 3 ? trimmed[3..].Trim() : "";
                bool parentEmit = ParentEmitting();
                bool take = parentEmit && Eval(expr);
                stack.Push((take, take));
                continue;
            }
            if (trimmed.StartsWith("#elif ", StringComparison.Ordinal))
            {
                if (stack.Count == 0) continue; // malformed, drop
                var (taken, _) = stack.Pop();
                var expr = trimmed[5..].Trim();
                bool parentEmit = ParentEmitting();
                bool take = parentEmit && !taken && Eval(expr);
                stack.Push((taken || take, take));
                continue;
            }
            if (trimmed.StartsWith("#else", StringComparison.Ordinal))
            {
                if (stack.Count == 0) continue;
                var (taken, _) = stack.Pop();
                bool parentEmit = ParentEmitting();
                bool take = parentEmit && !taken;
                stack.Push((true, take));
                continue;
            }
            if (trimmed.StartsWith("#endif", StringComparison.Ordinal))
            {
                if (stack.Count > 0) stack.Pop();
                continue;
            }

            if (ParentEmitting()) output.Add(line);
        }

        return string.Join('\n', output);
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
        if (needsTrunc) sb.Append("\n<!-- ...truncated -->");
        return sb.ToString();
    }

    private static string TruncateCSharp(string code, int maxChars)
    {
        if (code.Length <= maxChars) return code;
        int cut = code.LastIndexOf('\n', maxChars - 1);
        if (cut < 0) cut = maxChars;
        return code.Substring(0, cut).TrimEnd() + "\n// ...truncated";
    }

    private static string MakeScenarioId(string controlId, string header)
    {
        var slug = Regex.Replace(header.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 50) slug = slug.Substring(0, 50).TrimEnd('-');
        return $"{controlId}-{slug}";
    }

    // ─── Markdown frontmatter / tag extraction ─────────────────────────

    /// <summary>
    /// Extract author-curated keywords from the md frontmatter `keywords:`
    /// comma-separated list. These are the highest-quality intent signal
    /// available (hand-picked by toolkit authors) so they're surfaced as a
    /// separate, higher-weighted BM25 field — distinct from auto-extracted
    /// description tags which are noisier. Stop-word filtering still applies.
    /// </summary>
    private static string[] ExtractKeywords(string mdText)
    {
        var fm = ParseFrontmatter(mdText);
        if (!fm.TryGetValue("keywords", out var raw) || string.IsNullOrWhiteSpace(raw))
            return [];
        var list = new List<string>();
        var seen = new HashSet<string>();
        foreach (var k in raw.Split(','))
        {
            var t = k.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(t)) continue;
            if (StopWords.IsTagNoise(t)) continue;
            if (seen.Add(t)) list.Add(t);
        }
        return list.ToArray();
    }

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
                if (p.Length >= 3 && !StopWords.IsTagNoise(p)) tags.Add(p);
            }
        }
        // keywords (comma-separated)
        if (fm.TryGetValue("keywords", out var keywords))
        {
            foreach (var kw in keywords.Split(','))
            {
                var t = kw.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(t) && !StopWords.IsTagNoise(t)) tags.Add(t);
            }
        }
        // subcategory
        if (fm.TryGetValue("subcategory", out var sub))
        {
            var s = sub.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(s) && !StopWords.IsTagNoise(s)) tags.Add(s);
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
            if (StopWords.IsTagNoise(w) || !seen.Add(w)) continue;
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


