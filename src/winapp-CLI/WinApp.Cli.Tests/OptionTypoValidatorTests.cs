// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class OptionTypoValidatorTests : BaseCommandTests
{
    [TestMethod]
    public void FlagsSingleDashLongOptionAsTypo()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "ui", "inspect", "-app", "tauri-app", "--depth", "6" };
        var parseResult = rootCommand.Parse(args, WinAppParserConfiguration.Default);

        var typo = OptionTypoValidator.FindLikelyLongOptionTypo(args, parseResult);

        Assert.AreEqual("-app", typo);
    }

    [TestMethod]
    public void FlagsAttachedValueForm()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "ui", "inspect", "-app=tauri-app" };
        var parseResult = rootCommand.Parse(args, WinAppParserConfiguration.Default);

        var typo = OptionTypoValidator.FindLikelyLongOptionTypo(args, parseResult);

        Assert.AreEqual("-app", typo);
    }

    [TestMethod]
    public void DoesNotFlagValidShortOption()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "ui", "inspect", "-a", "tauri-app", "--depth", "6" };
        var parseResult = rootCommand.Parse(args, WinAppParserConfiguration.Default);

        var typo = OptionTypoValidator.FindLikelyLongOptionTypo(args, parseResult);

        Assert.IsNull(typo);
    }

    [TestMethod]
    public void DoesNotFlagValidLongOption()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "ui", "inspect", "--app", "tauri-app" };
        var parseResult = rootCommand.Parse(args, WinAppParserConfiguration.Default);

        var typo = OptionTypoValidator.FindLikelyLongOptionTypo(args, parseResult);

        Assert.IsNull(typo);
    }

    [TestMethod]
    public void DoesNotFlagUnknownShortClusterWithoutMatchingLong()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        // "-xyz" doesn't correspond to any "--xyz" option, so it's not a confident typo.
        var args = new[] { "ui", "inspect", "-xyz" };
        var parseResult = rootCommand.Parse(args, WinAppParserConfiguration.Default);

        var typo = OptionTypoValidator.FindLikelyLongOptionTypo(args, parseResult);

        Assert.IsNull(typo);
    }

    [TestMethod]
    public void IgnoresTokensAfterDoubleDash()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "ui", "inspect", "--app", "x", "--", "-app" };
        var parseResult = rootCommand.Parse(args, WinAppParserConfiguration.Default);

        var typo = OptionTypoValidator.FindLikelyLongOptionTypo(args, parseResult);

        Assert.IsNull(typo);
    }

    [TestMethod]
    public void IgnoresNegativeNumbers()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "ui", "inspect", "--app", "x", "-42" };
        var parseResult = rootCommand.Parse(args, WinAppParserConfiguration.Default);

        var typo = OptionTypoValidator.FindLikelyLongOptionTypo(args, parseResult);

        Assert.IsNull(typo);
    }

    [TestMethod]
    public void DoesNotFlagOnPassthroughCommand()
    {
        // Commands that pass unknown tokens through (ms-store, tool) must be honored, so even a
        // token that looks like a long-option typo is left untouched.
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "ms-store", "-app" };
        var parseResult = rootCommand.Parse(args, WinAppParserConfiguration.Default);

        var typo = OptionTypoValidator.FindLikelyLongOptionTypo(args, parseResult);

        Assert.IsNull(typo);
    }

    [TestMethod]
    public void ReturnsNullWhenLeafHasNoLongOptions()
    {
        // A command with no long options can't have a "did you mean --x?" candidate.
        var command = new Command("test");
        var args = new[] { "-ab" };
        var parseResult = command.Parse(args);

        var typo = OptionTypoValidator.FindLikelyLongOptionTypo(args, parseResult);

        Assert.IsNull(typo);
    }

    [TestMethod]
    public void DoesNotFlagTokenWithInvalidOptionCharacters()
    {
        // "-a.b" contains a '.', which is not a valid option-name character, so it is skipped.
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "ui", "inspect", "-a.b" };
        var parseResult = rootCommand.Parse(args, WinAppParserConfiguration.Default);

        var typo = OptionTypoValidator.FindLikelyLongOptionTypo(args, parseResult);

        Assert.IsNull(typo);
    }

    [TestMethod]
    public void FlagsTypoMatchingLongAlias()
    {
        // The candidate matches a long *alias* (not the primary name), exercising alias collection.
        var command = new Command("test");
        command.Options.Add(new Option<string>("--foo", "--foobar"));
        var args = new[] { "-foobar" };
        var parseResult = command.Parse(args);

        var typo = OptionTypoValidator.FindLikelyLongOptionTypo(args, parseResult);

        Assert.AreEqual("-foobar", typo);
    }
}
