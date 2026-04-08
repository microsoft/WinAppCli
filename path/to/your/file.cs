using System.Text.RegularExpressions;

public async Task<bool> EnsureRuntimeIdentifierAsync(string content)
{
    // Check if content already contains <RuntimeIdentifier ...> elements.
    if (RuntimeIdentifierElementRegex().IsMatch(content) || Regex.IsMatch(content, "<\s*RuntimeIdentifier\b[^>]*>", RegexOptions.IgnoreCase))
        return false;

    // The rest of the method remains unchanged...
    // Logic for adding the RuntimeIdentifier element.
}