// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WinApp.Cli.Services.Gallery;


internal static partial class GalleryFetcher
{
    private const string ControlInfoUrl =
        "https://raw.githubusercontent.com/microsoft/WinUI-Gallery/main/WinUIGallery/Samples/Data/ControlInfoData.json";
    private const string ControlPagesBase =
        "https://raw.githubusercontent.com/microsoft/WinUI-Gallery/main/WinUIGallery/Samples/ControlPages/";
    private const string SampleCodeBase =
        "https://raw.githubusercontent.com/microsoft/WinUI-Gallery/main/WinUIGallery/Samples/SampleCode/";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    /// <summary>Bump when the cached JSON schema changes (e.g., new fields on Scenario).</summary>
    private const string CacheSchemaVersion = "2";

    private static readonly HttpClient Http = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "winui-gallery-cli/1.0" } }
    };

    [GeneratedRegex(@"<controls:ControlExample\b[^>]*?HeaderText=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ControlExampleHeaderRegex();

    [GeneratedRegex(@"CSharpSource=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CSharpSourceRegex();

    [GeneratedRegex(@"XamlSource=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex XamlSourceRegex();

    [GeneratedRegex(@"<controls:ControlExample\.(Xaml|CSharp)>\s*<x:String[^>]*>([\s\S]*?)</x:String>\s*</controls:ControlExample\.\1>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"\$\([^)]+\)")]
    private static partial Regex SubstitutionRegex();

    [GeneratedRegex(@"ms-appx:///Assets/SampleMedia/[^""'\s]+")]
    private static partial Regex SampleMediaRegex();

    [GeneratedRegex(@"x:Class=""WinUIGallery\.[^""]+""")]
    private static partial Regex GalleryClassRegex();

    [GeneratedRegex(@"typeof\(SamplePage\d+\)")]
    private static partial Regex SamplePageTypeRegex();

    [GeneratedRegex(@"SamplePage\d+")]
    private static partial Regex SamplePageNameRegex();

    /// <summary>Load scenarios + tags: use cache if fresh, otherwise fetch from GitHub. Fallback to embedded.</summary>
    public static (Scenario[] scenarios, Dictionary<string, string[]> tags) Load(string cacheDir)
    {
        var scenarioCache = Path.Combine(cacheDir, "scenarios.json");
        var tagCache = Path.Combine(cacheDir, "tags.json");
        var timestampFile = Path.Combine(cacheDir, "last-updated.txt");
        var versionFile = Path.Combine(cacheDir, "schema-version.txt");

        // Check cache freshness AND schema version
        if (File.Exists(scenarioCache) && File.Exists(tagCache) && File.Exists(timestampFile) && File.Exists(versionFile))
        {
            var cachedVersion = File.ReadAllText(versionFile).Trim();
            if (cachedVersion == CacheSchemaVersion
                && DateTime.TryParse(File.ReadAllText(timestampFile).Trim(), out var lastUpdated)
                && DateTime.UtcNow - lastUpdated < CacheTtl)
            {
                try
                {
                    var s = JsonSerializer.Deserialize(File.ReadAllText(scenarioCache), GalleryJsonContext.Default.ScenarioArray);
                    var t = JsonSerializer.Deserialize(File.ReadAllText(tagCache), GalleryJsonContext.Default.DictionaryStringStringArray);
                    if (s != null && s.Length > 0 && t != null) return (s, t);
                }
                catch { /* fall through to fetch */ }
            }
        }

        // Try fetching from GitHub
        try
        {
            var (scenarios, tags) = FetchFromGitHub().GetAwaiter().GetResult();
            if (scenarios.Length > 0)
            {
                ApplyOverrides(scenarios);
                scenarios = InjectMissing(scenarios);
                Directory.CreateDirectory(cacheDir);
                File.WriteAllText(scenarioCache, JsonSerializer.Serialize(scenarios, GalleryJsonContext.Default.ScenarioArray));
                File.WriteAllText(tagCache, JsonSerializer.Serialize(tags, GalleryJsonContext.Default.DictionaryStringStringArray));
                File.WriteAllText(timestampFile, DateTime.UtcNow.ToString("o"));
                File.WriteAllText(versionFile, CacheSchemaVersion);
                return (scenarios, tags);
            }
        }
        catch
        {
            // Fall through to embedded data
        }

        // Fallback to embedded (apply stop-word filter to hand-curated tags too)
        return (DataLoader.LoadGalleryScenarios(), CleanTags(DataLoader.LoadGalleryTags()));
    }

    private static async Task<(Scenario[], Dictionary<string, string[]>)> FetchFromGitHub()
    {
        // Step 1: Fetch ControlInfoData.json — list of controls + their Subtitle (used for tags)
        var infoJson = await Http.GetStringAsync(ControlInfoUrl);
        using var doc = JsonDocument.Parse(infoJson);
        var groups = doc.RootElement.GetProperty("Groups");

        var controlPages = new List<(string uniqueId, string title, string? folder)>();
        var subtitles = new Dictionary<string, string>();  // controlId → Subtitle text
        foreach (var group in groups.EnumerateArray())
        {
            string? folder = null;
            if (group.TryGetProperty("IsSpecialSection", out var isSpecial) && isSpecial.GetBoolean()
                && group.TryGetProperty("Folder", out var folderProp))
            {
                folder = folderProp.GetString();
            }

            if (!group.TryGetProperty("Items", out var items)) continue;
            foreach (var item in items.EnumerateArray())
            {
                var uniqueId = item.GetProperty("UniqueId").GetString() ?? "";
                var title = item.GetProperty("Title").GetString() ?? "";
                controlPages.Add((uniqueId, title, folder));

                string subtitle = "";
                if (item.TryGetProperty("Subtitle", out var sub)) subtitle = sub.GetString() ?? "";
                subtitles[uniqueId.ToLowerInvariant()] = $"{title} {subtitle}".Trim();
            }
        }

        // Step 2: Fetch each control page and parse ControlExample blocks
        var allScenarios = new List<Scenario>();
        var fetchTasks = new List<Task<List<Scenario>>>();
        var semaphore = new SemaphoreSlim(10);
        foreach (var (uniqueId, title, folder) in controlPages)
        {
            fetchTasks.Add(FetchControlPageAsync(uniqueId, title, folder, semaphore));
        }
        var results = await Task.WhenAll(fetchTasks);
        foreach (var batch in results)
            allScenarios.AddRange(batch);

        // Step 3: Build tags. For each control, prefer hand-curated embedded tags;
        // otherwise auto-derive from Title + Subtitle. Always strip stop words.
        var embeddedTags = DataLoader.LoadGalleryTags();
        var allTags = new Dictionary<string, string[]>();
        foreach (var (controlId, text) in subtitles)
        {
            if (embeddedTags.TryGetValue(controlId, out var manual))
            {
                allTags[controlId] = FilterStopWords(manual);
            }
            else
            {
                allTags[controlId] = ExtractTagsFromText(controlId, text);
            }
        }
        // Also include any embedded tags for controls not in ControlInfoData (jumplist-* etc.)
        foreach (var (k, v) in embeddedTags)
        {
            if (!allTags.ContainsKey(k)) allTags[k] = FilterStopWords(v);
        }

        return (allScenarios.ToArray(), allTags);
    }

    private static string[] FilterStopWords(string[] tags)
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var t in tags)
        {
            var lower = t.ToLowerInvariant();
            if (StopWords.Common.Contains(lower)) continue;
            if (seen.Add(lower)) result.Add(lower);
        }
        return result.ToArray();
    }

    private static string[] ExtractTagsFromText(string controlId, string text)
    {
        var tags = new List<string> { controlId };
        var seen = new HashSet<string> { controlId };
        // Split CamelCase/PascalCase into separate words too
        text = Regex.Replace(text, @"(?<=[a-z])(?=[A-Z])", " ");
        foreach (Match m in Regex.Matches(text.ToLowerInvariant(), @"[a-z]{3,}"))
        {
            var w = m.Value;
            if (StopWords.Common.Contains(w)) continue;
            if (seen.Add(w)) tags.Add(w);
            if (tags.Count >= 12) break;
        }
        return tags.ToArray();
    }

    private static Dictionary<string, string[]> CleanTags(Dictionary<string, string[]> tags)
    {
        var result = new Dictionary<string, string[]>(tags.Count);
        foreach (var (k, v) in tags) result[k] = FilterStopWords(v);
        return result;
    }

    private static async Task<List<Scenario>> FetchControlPageAsync(
        string uniqueId, string title, string? folder, SemaphoreSlim semaphore)
    {
        var scenarios = new List<Scenario>();
        await semaphore.WaitAsync();
        try
        {
            var pagePath = folder != null
                ? $"{folder}/{uniqueId}Page.xaml"
                : $"{uniqueId}Page.xaml";
            var url = ControlPagesBase + pagePath;

            var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return scenarios;

            var xamlContent = await response.Content.ReadAsStringAsync();
            var controlId = uniqueId.ToLowerInvariant();
            int scenarioIndex = 0;
            var seenIds = new HashSet<string>();

            foreach (var (headerText, block) in ExtractControlExampleBlocks(xamlContent))
            {
                // Extract code: external file or inline
                string? csharp = await ExtractCode(block, "CSharp", CSharpSourceRegex());
                string? xaml = await ExtractCode(block, "Xaml", XamlSourceRegex());

                // Try inline if external not found
                csharp ??= ExtractInlineCode(block, "CSharp");
                xaml ??= ExtractInlineCode(block, "Xaml");

                if (csharp == null && xaml == null) continue;

                if (xaml != null) xaml = TruncateXaml(xaml, MaxXamlChars);
                if (csharp != null) csharp = TruncateCode(csharp, MaxCSharpChars, "// NOTE: snippet truncated — refer to full sample for additional code");

                scenarioIndex++;
                var slug = HeaderToSlug(headerText);
                var baseId = scenarioIndex == 1 ? controlId : $"{controlId}-{slug}";
                var scenarioId = baseId;
                int suffix = 2;
                while (!seenIds.Add(scenarioId))
                {
                    scenarioId = $"{baseId}-{suffix}";
                    suffix++;
                }

                scenarios.Add(new Scenario
                {
                    Id = scenarioId,
                    ControlId = controlId,
                    ControlName = title,
                    HeaderText = headerText,
                    Xaml = xaml,
                    CSharp = csharp,
                    Source = "gallery",
                });
            }
        }
        catch { /* skip this control */ }
        finally { semaphore.Release(); }

        return scenarios;
    }

    private const int MaxXamlChars = 2000;
    private const int MaxCSharpChars = 1500;

    [GeneratedRegex(@"<(/?)([A-Za-z_][\w:.\-]*)\b([^>]*?)(/?)>")]
    private static partial Regex AnyTagRegex();

    /// <summary>
    /// Find all top-level &lt;controls:ControlExample&gt; blocks via stack-aware tag matching,
    /// handling nested ScrollViewer/StackPanel inside ControlExample.Example.
    /// </summary>
    private static IEnumerable<(string headerText, string block)> ExtractControlExampleBlocks(string xaml)
    {
        const string OpenTag = "<controls:ControlExample";
        const string CloseTag = "</controls:ControlExample>";
        int searchStart = 0;
        while (true)
        {
            int openIdx = xaml.IndexOf(OpenTag, searchStart, StringComparison.OrdinalIgnoreCase);
            if (openIdx < 0) yield break;

            // Make sure this isn't ControlExample.Xaml or ControlExample.Example etc.
            int afterPrefix = openIdx + OpenTag.Length;
            if (afterPrefix < xaml.Length && (xaml[afterPrefix] == '.' || char.IsLetterOrDigit(xaml[afterPrefix])))
            {
                searchStart = openIdx + OpenTag.Length;
                continue;
            }

            // Find end of opening tag '>'
            int openTagEnd = xaml.IndexOf('>', afterPrefix);
            if (openTagEnd < 0) yield break;

            // Self-closing? Skip.
            if (xaml[openTagEnd - 1] == '/')
            {
                searchStart = openTagEnd + 1;
                continue;
            }

            // Extract header text from opening tag attributes
            string openingTag = xaml.Substring(openIdx, openTagEnd - openIdx + 1);
            var headerMatch = ControlExampleHeaderRegex().Match(openingTag);
            string headerText = headerMatch.Success ? headerMatch.Groups[1].Value : "";

            // Walk forward, balancing <controls:ControlExample> tags
            int depth = 1;
            int pos = openTagEnd + 1;
            int blockEnd = -1;
            while (pos < xaml.Length)
            {
                int nextOpen = xaml.IndexOf(OpenTag, pos, StringComparison.OrdinalIgnoreCase);
                int nextClose = xaml.IndexOf(CloseTag, pos, StringComparison.OrdinalIgnoreCase);
                if (nextClose < 0) break;
                if (nextOpen >= 0 && nextOpen < nextClose)
                {
                    int after = nextOpen + OpenTag.Length;
                    // Skip ControlExample.Xaml/.CSharp/.Example sub-properties (they don't open a new block)
                    if (after < xaml.Length && xaml[after] != '.' && !char.IsLetterOrDigit(xaml[after]))
                    {
                        int gt = xaml.IndexOf('>', after);
                        if (gt > 0 && xaml[gt - 1] != '/') depth++;
                        pos = gt > 0 ? gt + 1 : nextOpen + OpenTag.Length;
                        continue;
                    }
                    pos = after;
                }
                else
                {
                    depth--;
                    if (depth == 0)
                    {
                        blockEnd = nextClose + CloseTag.Length;
                        break;
                    }
                    pos = nextClose + CloseTag.Length;
                }
            }

            if (blockEnd < 0) yield break;
            yield return (headerText, xaml.Substring(openIdx, blockEnd - openIdx));
            searchStart = blockEnd;
        }
    }

    /// <summary>Truncate XAML at a safe boundary, appending closing tags for unclosed elements.</summary>
    private static string TruncateXaml(string xaml, int maxChars)
    {
        bool needsTruncate = xaml.Length > maxChars;
        string head;
        if (needsTruncate)
        {
            // Find a safe '>' boundary
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

        // Count open/close tags
        var stack = new Stack<string>();
        bool sawMismatch = false;
        foreach (Match m in AnyTagRegex().Matches(head))
        {
            bool isClose = m.Groups[1].Value == "/";
            bool isSelf = m.Groups[4].Value == "/";
            string name = m.Groups[2].Value;
            if (isSelf) continue;
            if (isClose)
            {
                if (stack.Count > 0 && stack.Peek() == name) stack.Pop();
                else sawMismatch = true;
            }
            else
            {
                stack.Push(name);
            }
        }

        // If balanced and not truncated, return original
        if (!needsTruncate && stack.Count == 0 && !sawMismatch) return xaml;

        var sb = new System.Text.StringBuilder(head.TrimEnd());
        while (stack.Count > 0) sb.Append("</").Append(stack.Pop()).Append('>');
        if (needsTruncate) sb.Append("\n<!-- NOTE: XAML truncated — additional sibling elements omitted -->");
        return sb.ToString();
    }

    /// <summary>Truncate code at line boundary with a comment marker.</summary>
    private static string TruncateCode(string code, int maxChars, string marker)
    {
        if (code.Length <= maxChars) return code;
        int cut = code.LastIndexOf('\n', maxChars - 1);
        if (cut < 0) cut = maxChars;
        return code.Substring(0, cut).TrimEnd() + "\n" + marker;
    }

    private static async Task<string?> ExtractCode(string block, string type, Regex sourceRegex)
    {
        var sourceMatch = sourceRegex.Match(block);
        if (!sourceMatch.Success) return null;

        var relativePath = sourceMatch.Groups[1].Value.Replace('\\', '/');
        var url = SampleCodeBase + relativePath;

        try
        {
            var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            var code = await response.Content.ReadAsStringAsync();
            return CleanGalleryContent(code.Trim());
        }
        catch { return null; }
    }

    private static string? ExtractInlineCode(string block, string tagName)
    {
        var pattern = $@"<controls:ControlExample\.{tagName}>\s*<x:String[^>]*>([\s\S]*?)</x:String>\s*</controls:ControlExample\.{tagName}>";
        var match = Regex.Match(block, pattern, RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var code = UnescapeXml(match.Groups[1].Value).Trim();
        if (code.Contains("$("))
            code = SubstitutionRegex().Replace(code, "...");
        code = CleanGalleryContent(code);
        return string.IsNullOrWhiteSpace(code) ? null : code;
    }

    private static string CleanGalleryContent(string code)
    {
        code = SampleMediaRegex().Replace(code, "ms-appx:///Assets/YourImage.png");
        code = GalleryClassRegex().Replace(code, @"x:Class=""YourApp.YourPage""");
        code = SamplePageTypeRegex().Replace(code, "typeof(YourPage)");
        code = SamplePageNameRegex().Replace(code, "YourPage");
        code = Regex.Replace(code, @"using WinUIGallery[^;\n]*", "// adapt namespace to your app");
        code = Regex.Replace(code, @"using AppUIBasics[^;\n]*", "// adapt namespace to your app");
        code = Regex.Replace(code, @"namespace WinUIGallery[^{\n]*", "namespace YourApp");
        code = Regex.Replace(code, @"namespace AppUIBasics[^{\n]*", "namespace YourApp");
        code = Regex.Replace(code, @".*NavigationHelper.*\n?", "");

        // Clean demo-specific layout attributes (fixed sizes, negative margins, demo handlers)
        var lines = code.Split('\n').Where(line =>
        {
            var trimmed = line.Trim();
            if (Regex.IsMatch(trimmed, @"^(Min|Max)?(Height|Width)=""\d")) return false;
            if (Regex.IsMatch(trimmed, @"^Margin=""-")) return false;
            if (Regex.IsMatch(trimmed, @"^Loaded=""[^""]*_Loaded""")) return false;
            if (Regex.IsMatch(trimmed, @"^SelectedIndex=""\d+""")) return false;
            return true;
        });
        code = string.Join('\n', lines);

        // Clean substitution placeholders: replace known $(...) or "..." with defaults
        code = Regex.Replace(code, @"IsOpen=""(\$\(IsOpen\)|\.\.\.?)""", @"IsOpen=""True""");
        code = Regex.Replace(code, @"Severity=""(\$\(Severity\)|\.\.\.?)""", @"Severity=""Informational""");
        code = SubstitutionRegex().Replace(code, "...");

        code = Regex.Replace(code, @"\n\s*\n\s*\n", "\n\n");
        return code.Trim();
    }

    private static string UnescapeXml(string s)
    {
        return s.Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&amp;", "&")
                .Replace("&quot;", "\"")
                .Replace("&apos;", "'")
                .Replace("&#10;", "\n")
                .Replace("&#13;", "\r");
    }

    private static string HeaderToSlug(string header)
    {
        return Regex.Replace(header.ToLowerInvariant(), @"[^a-z0-9\s]", "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(4)
            .Aggregate((a, b) => a + "-" + b);
    }

    /// <summary>
    /// Override Gallery demo code with production-quality snippets where the
    /// original is known to mislead agents (e.g., TabView using Frame instead of direct content).
    /// </summary>
    private static void ApplyOverrides(Scenario[] scenarios)
    {
        foreach (var s in scenarios)
        {
            if (s.Id == "tabview" && s.CSharp != null && s.CSharp.Contains("Frame"))
            {
                s.CSharp = """
                    private void TabView_AddButtonClick(TabView sender, object args)
                    {
                        sender.TabItems.Add(CreateNewTab(sender.TabItems.Count));
                    }

                    private void TabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
                    {
                        sender.TabItems.Remove(args.Tab);
                    }

                    private TabViewItem CreateNewTab(int index)
                    {
                        TabViewItem newItem = new TabViewItem();
                        newItem.Header = $"Document {index}";
                        newItem.IconSource = new SymbolIconSource() { Symbol = Symbol.Document };
                        newItem.IsClosable = true;

                        // Content can be any UIElement — TextBox, Grid, UserControl, etc.
                        var textBox = new TextBox
                        {
                            AcceptsReturn = true,
                            TextWrapping = TextWrapping.Wrap,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            VerticalAlignment = VerticalAlignment.Stretch,
                            BorderThickness = new Thickness(0),
                        };
                        newItem.Content = textBox;

                        return newItem;
                    }
                    """;
            }
        }
    }

    /// <summary>Inject scenarios for controls that have no ControlExample code in the Gallery.</summary>
    private static Scenario[] InjectMissing(Scenario[] scenarios)
    {
        var ids = new HashSet<string>(scenarios.Select(s => s.ControlId));
        var injected = new List<Scenario>(scenarios);

        if (!ids.Contains("commandbar"))
        {
            injected.Add(new Scenario
            {
                Id = "commandbar",
                ControlId = "commandbar",
                ControlName = "CommandBar",
                HeaderText = "A CommandBar with primary and secondary commands",
                Xaml = """
                    <CommandBar DefaultLabelPosition="Right">
                        <AppBarButton Icon="Add" Label="Add" Click="AddButton_Click"/>
                        <AppBarButton Icon="Edit" Label="Edit" Click="EditButton_Click"/>
                        <AppBarButton Icon="Delete" Label="Delete" Click="DeleteButton_Click"/>
                        <AppBarSeparator/>
                        <AppBarButton Icon="Refresh" Label="Refresh" Click="RefreshButton_Click"/>
                        <CommandBar.SecondaryCommands>
                            <AppBarButton Icon="Setting" Label="Settings"/>
                            <AppBarButton Icon="Help" Label="About"/>
                        </CommandBar.SecondaryCommands>
                    </CommandBar>
                    """,
                CSharp = null
            });
        }

        // CommunityToolkit controls now come from toolkit-scenarios.json
        // Only CommandBar needs injection (no ControlExample in WinUI Gallery)

        return injected.ToArray();
    }
}

