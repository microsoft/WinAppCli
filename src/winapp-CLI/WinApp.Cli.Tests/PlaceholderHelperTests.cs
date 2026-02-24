// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class PlaceholderHelperTests
{
    [TestMethod]
    public void ReplacePlaceholders_WithMatchingTokens_ReplacesAll()
    {
        var content = @"Executable=""$targetnametoken$.exe"" EntryPoint=""$targetentrypoint$""";
        var replacements = new Dictionary<string, string>
        {
            { PlaceholderHelper.TargetNameToken, "MyApp" }
        };

        var result = PlaceholderHelper.ReplacePlaceholders(content, replacements);

        Assert.AreEqual(@"Executable=""MyApp.exe"" EntryPoint=""Windows.FullTrustApplication""", result);
    }

    [TestMethod]
    public void ReplacePlaceholders_CaseInsensitive_ReplacesAll()
    {
        var content = @"$TargetNameToken$.exe";
        var replacements = new Dictionary<string, string>
        {
            { "targetnametoken", "MyApp" }
        };

        var result = PlaceholderHelper.ReplacePlaceholders(content, replacements);

        Assert.AreEqual("MyApp.exe", result);
    }

    [TestMethod]
    public void ReplacePlaceholders_NoMatchingTokens_LeavesUnchanged()
    {
        var content = @"Executable=""$unknowntoken$.exe""";
        var replacements = new Dictionary<string, string>
        {
            { "targetnametoken", "MyApp" }
        };

        var result = PlaceholderHelper.ReplacePlaceholders(content, replacements);

        Assert.AreEqual(@"Executable=""$unknowntoken$.exe""", result);
    }

    [TestMethod]
    public void ReplacePlaceholders_EmptyContent_ReturnsEmpty()
    {
        var result = PlaceholderHelper.ReplacePlaceholders("", new Dictionary<string, string> { { "key", "value" } });

        Assert.AreEqual("", result);
    }

    [TestMethod]
    public void ReplacePlaceholders_NullContent_ReturnsNull()
    {
        var result = PlaceholderHelper.ReplacePlaceholders(null!, new Dictionary<string, string> { { "key", "value" } });

        Assert.IsNull(result);
    }

    [TestMethod]
    public void ReplacePlaceholders_EmptyReplacements_ReturnsOriginal()
    {
        var content = @"$targetnametoken$.exe";

        var result = PlaceholderHelper.ReplacePlaceholders(content, new Dictionary<string, string>());

        Assert.AreEqual(content, result);
    }

    [TestMethod]
    public void ReplacePlaceholders_AutoResolvesTargetEntryPoint()
    {
        var content = @"EntryPoint=""$targetentrypoint$""";

        // No explicit replacement for targetentrypoint — should be resolved automatically
        var result = PlaceholderHelper.ReplacePlaceholders(content, new Dictionary<string, string>());

        Assert.AreEqual(@"EntryPoint=""Windows.FullTrustApplication""", result);
    }

    [TestMethod]
    public void ReplacePlaceholders_NoArgOverload_ResolvesBuiltIns()
    {
        var content = @"Executable=""$targetnametoken$.exe"" EntryPoint=""$targetentrypoint$""";

        var result = PlaceholderHelper.ReplacePlaceholders(content);

        Assert.Contains(@"EntryPoint=""Windows.FullTrustApplication""", result, "Built-in $targetentrypoint$ should be resolved");
        Assert.Contains("$targetnametoken$", result, "Non-built-in $targetnametoken$ should be left as-is");
    }

    [TestMethod]
    public void ThrowIfUnresolvedPlaceholders_WithPlaceholders_Throws()
    {
        var content = @"Executable=""$targetnametoken$.exe"" EntryPoint=""$targetentrypoint$""";

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => PlaceholderHelper.ThrowIfUnresolvedPlaceholders(content));

        Assert.Contains("$targetnametoken$", ex.Message, "Error should mention targetnametoken");
        Assert.Contains("$targetentrypoint$", ex.Message, "Error should mention targetentrypoint");
    }

    [TestMethod]
    public void ThrowIfUnresolvedPlaceholders_NoPlaceholders_DoesNotThrow()
    {
        var content = @"Executable=""MyApp.exe"" EntryPoint=""Windows.FullTrustApplication""";

        PlaceholderHelper.ThrowIfUnresolvedPlaceholders(content);
    }

    [TestMethod]
    public void ThrowIfUnresolvedPlaceholders_EmptyContent_DoesNotThrow()
    {
        PlaceholderHelper.ThrowIfUnresolvedPlaceholders("");
    }

    [TestMethod]
    public void ThrowIfUnresolvedPlaceholders_NullContent_DoesNotThrow()
    {
        PlaceholderHelper.ThrowIfUnresolvedPlaceholders(null!);
    }

    [TestMethod]
    public void ThrowIfUnresolvedPlaceholders_DuplicatePlaceholders_ListsOnce()
    {
        var content = @"$targetnametoken$.exe and $targetnametoken$.dll";

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => PlaceholderHelper.ThrowIfUnresolvedPlaceholders(content));

        // Should mention it once, not twice
        var occurrences = ex.Message.Split("$targetnametoken$").Length - 1;
        Assert.AreEqual(1, occurrences, "Duplicate placeholders should be listed once");
    }

    [TestMethod]
    public void ContainsPlaceholders_WithPlaceholder_ReturnsTrue()
    {
        Assert.IsTrue(PlaceholderHelper.ContainsPlaceholders("$targetnametoken$.exe"));
    }

    [TestMethod]
    public void ContainsPlaceholders_WithoutPlaceholder_ReturnsFalse()
    {
        Assert.IsFalse(PlaceholderHelper.ContainsPlaceholders("MyApp.exe"));
    }

    [TestMethod]
    public void ContainsPlaceholders_NullValue_ReturnsFalse()
    {
        Assert.IsFalse(PlaceholderHelper.ContainsPlaceholders(null));
    }

    [TestMethod]
    public void ContainsPlaceholders_EmptyValue_ReturnsFalse()
    {
        Assert.IsFalse(PlaceholderHelper.ContainsPlaceholders(""));
    }

    [TestMethod]
    public void ContainsPlaceholders_DollarSignsNotMatching_ReturnsFalse()
    {
        // Single dollar sign or non-matching patterns
        Assert.IsFalse(PlaceholderHelper.ContainsPlaceholders("$100.00"));
        Assert.IsFalse(PlaceholderHelper.ContainsPlaceholders("price is $5"));
    }

    [TestMethod]
    public void ReplacePlaceholders_MultipleOccurrences_ReplacesAll()
    {
        var content = @"Name=""$targetnametoken$"" Exe=""$targetnametoken$.exe""";
        var replacements = new Dictionary<string, string>
        {
            { "targetnametoken", "MyApp" }
        };

        var result = PlaceholderHelper.ReplacePlaceholders(content, replacements);

        Assert.AreEqual(@"Name=""MyApp"" Exe=""MyApp.exe""", result);
    }

    [TestMethod]
    public void ReplacePlaceholders_FullManifestSnippet_ResolvesCorrectly()
    {
        var content = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package>
  <Applications>
    <Application Id=""App""
      Executable=""$targetnametoken$.exe""
      EntryPoint=""$targetentrypoint$"">
    </Application>
  </Applications>
</Package>";

        var replacements = new Dictionary<string, string>
        {
            { PlaceholderHelper.TargetNameToken, "MyApp" }
        };

        var result = PlaceholderHelper.ReplacePlaceholders(content, replacements);

        Assert.Contains(@"Executable=""MyApp.exe""", result, "Executable should be resolved");
        Assert.Contains(@"EntryPoint=""Windows.FullTrustApplication""", result, "EntryPoint should be resolved");
        Assert.IsFalse(PlaceholderHelper.ContainsPlaceholders(result), "No placeholders should remain");
    }
}
