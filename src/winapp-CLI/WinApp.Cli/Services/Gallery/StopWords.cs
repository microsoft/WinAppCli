// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Gallery;

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
        "displays","display","presents","shows","show","lets","let",
        "between","while",
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
    };
}
