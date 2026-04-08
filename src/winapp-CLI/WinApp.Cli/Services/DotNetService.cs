        // Check if the content contains any <RuntimeIdentifier ...> opening tag
        if (RuntimeIdentifierElementRegex().IsMatch(content) || Regex.IsMatch(content, @"<\s*RuntimeIdentifier\b[^>]*>", RegexOptions.IgnoreCase)) return false;
        
        // Optionally, keep the rest of the method unchanged as per requirement.
