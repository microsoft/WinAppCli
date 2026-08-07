// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

internal static class StopWords
{
    /// <summary>
    /// Common words that don't help discriminate between controls.
    /// Used by both Gallery and Toolkit tag extractors.
    /// Note: BM25 already does IDF weighting, but stripping these saves space.
    /// </summary>
    public static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        // Articles, conjunctions, pronouns, prepositions
        "the","a","an","and","or","is","are","was","were","be","been",
        "can","will","that","this","it","its","in","on","of","to","for",
        "with","by","from","as","at","has","have","had","not","but",
        "all","any","each","how","when","where","which","who","you","your",
        "we","our","they","them","their","also","more","than","like","just",
        "about","into","over","such","only","very","well","see",
        // Generic verbs
        "use","used","using","set","get","make","made",
        "displays","display","displaying","presents","shows","show","lets","let",
        "between","while","contain","contains","containing",
        "maintain","maintains","maintaining",
        // Generic UI nouns (no discrimination value across many controls)
        "control","controls","property","properties","value","values",
        "default","custom","new","component","sample","example",
        "user","content","app","item","items","element","elements",
        // Tech terms / infra
        "csharp","xaml","uwp","winui","communitytoolkit",
        // Description filler from WCT docs
        "some","note","however","should","similar","objects","object","allows",
        "setting","based","usage","having","public","instead","provide","found",
        "does","support","many","main","case","create","certain",
        "depending","technical","reasons","inherits","treated","added","whichever",
        "acts","provided","subsequent","happens","sequentially","pretty","shares",
        "needs","generate","accessible","replacement","look","specify",
        "interface","represents","source","whose","loaded","ienumerable",
        "instances","instance","examples",
        // Auto-extraction noise from sample C#/XAML / commit messages
        "true","false","pass","done","constructor",
        "functionality","effect","through","various","modern","easy","simple",
        "kind","type","types","approach","amount","least","space",
        // Low-signal words common in Gallery sample HeaderText
        "basic","adding","changes","header","options","another",
    };

    /// <summary>
    /// Words that pollute control tag lists but a user might legitimately type as
    /// a query token. Removed from tag dictionaries (in <see cref="FilterTagList"/>)
    /// but NOT from <c>BM25.Tokenize</c>, so queries like "text editor" still see
    /// the "text" token and match TextBox via its nameSplit field.
    /// </summary>
    /// <remarks>
    /// Why this matters: control docs contain a SplitCamelCase field
    /// (TextBox → "text"+"box", CalendarDatePicker → "calendar"+"date"+"picker"),
    /// so removing these tokens from enrichment tags does NOT lose the canonical
    /// match — only the spurious matches (Clipboard's "text", ColorPicker's
    /// "text/input/layout/pick", every panel's "layout" subcategory).
    /// </remarks>
    public static readonly HashSet<string> TagOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "text",     // Clipboard/HyperlinkButton/RichSuggestBox/ColorPicker noise; Text* controls keep it via nameSplit
        "input",    // Button/ColorPicker/RangeSelector noise; *Box controls keep it via nameSplit
        "layout",   // Toolkit subcategory — auto-stamped onto 18 controls, near-zero IDF
        "pick",     // Picker controls keep "picker" via nameSplit; "pick a X" queries match via X
        "basics",   // section heading filler
        "advanced", // AdvancedCollectionView keeps it via nameSplit
    };

    /// <summary>True if the token should be dropped from BOTH BM25 query tokens AND tag dicts.</summary>
    public static bool IsCommon(string w) => Common.Contains(w);

    /// <summary>True if the token should be dropped from tag dicts (a superset of <see cref="IsCommon"/>).</summary>
    public static bool IsTagNoise(string w) => Common.Contains(w) || TagOnly.Contains(w);

    /// <summary>
    /// Drops stop words (Common + TagOnly), sample-suffix tokens (e.g.,
    /// "imagecroppersample"), and dedupes. Multi-word tags ("context menu") are
    /// preserved as-is. Use this for tag dictionaries; for BM25 query
    /// tokenization use <see cref="IsCommon(string)"/> directly.
    /// </summary>
    public static string[] FilterTagList(string[] tags)
    {
        var seen = new HashSet<string>();
        var result = new List<string>(tags.Length);
        foreach (var t in tags)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            var lower = t.Trim().ToLowerInvariant();
            if (IsTagNoise(lower)) continue;
            // Drop auto-extracted *sample suffix tokens like "imagecroppersample"
            // (only single-word tokens; preserve multi-word tags as-is).
            if (!lower.Contains(' ') && lower.Length > "sample".Length && lower.EndsWith("sample"))
                continue;
            if (seen.Add(lower)) result.Add(lower);
        }
        return result.ToArray();
    }

    /// <summary>Apply <see cref="FilterTagList"/> to every entry of a tag dictionary.</summary>
    public static Dictionary<string, string[]> CleanTagDictionary(Dictionary<string, string[]> tags)
    {
        var result = new Dictionary<string, string[]>(tags.Count);
        foreach (var (k, v) in tags) result[k] = FilterTagList(v);
        return result;
    }
}
