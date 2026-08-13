// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>
/// Lexical relevance scoring for the <c>find-api</c> search: exact &gt; prefix &gt;
/// contains &gt; acronym &gt; all-terms &gt; fuzzy subsequence.
/// </summary>
internal static class Scoring
{
    public static int GetMatchScore(string name, string fullName, string query)
    {
        string text = query.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        if (name.Equals(text, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }
        if (name.StartsWith(text, StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }
        if (name.Contains(text, StringComparison.OrdinalIgnoreCase) || fullName.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return 60;
        }
        string acronym = new string(name.Where(char.IsUpper).ToArray());
        if (acronym.Length >= 2 && acronym.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }
        string[] terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length > 1)
        {
            bool allTermsMatch = terms.All(term =>
                name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || fullName.Contains(term, StringComparison.OrdinalIgnoreCase));
            if (allTermsMatch)
            {
                return 40;
            }
        }
        if (IsFuzzySubsequence(name, text))
        {
            return 20;
        }
        return 0;
    }

    private static bool IsFuzzySubsequence(string text, string pattern)
    {
        string haystack = text.ToLowerInvariant();
        string needle = pattern.ToLowerInvariant();
        int startIndex = 0;
        foreach (char c in needle)
        {
            int idx = haystack.IndexOf(c, startIndex);
            if (idx < 0)
            {
                return false;
            }
            startIndex = idx + 1;
        }
        return true;
    }
}
