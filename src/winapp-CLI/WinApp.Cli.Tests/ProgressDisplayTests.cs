// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Helpers;
using WinApp.Cli.Telemetry;

namespace WinApp.Cli.Tests;

[TestClass]
public class ProgressDisplayTests
{
    private const string Direct = AgentEnvironmentDetector.SenderOrigins.Direct;
    private const string Agent = AgentEnvironmentDetector.SenderOrigins.Agent;
    private const string CI = AgentEnvironmentDetector.SenderOrigins.CI;

    [TestMethod]
    public void ShouldUseLiveSpinner_InfoDisabled_ReturnsFalse()
    {
        // Even with an otherwise perfect interactive terminal, suppressed info output => no spinner.
        Assert.IsFalse(ProgressDisplay.ShouldUseLiveSpinner(
            infoEnabled: false, senderOrigin: Direct, interactiveCapability: true,
            ansiCapability: true, outputRedirected: false, userInteractive: true));
    }

    [TestMethod]
    public void ShouldUseLiveSpinner_AgentOrigin_ReturnsFalse()
    {
        Assert.IsFalse(ProgressDisplay.ShouldUseLiveSpinner(
            infoEnabled: true, senderOrigin: Agent, interactiveCapability: true,
            ansiCapability: true, outputRedirected: false, userInteractive: true));
    }

    [TestMethod]
    public void ShouldUseLiveSpinner_CiOrigin_ReturnsFalse()
    {
        Assert.IsFalse(ProgressDisplay.ShouldUseLiveSpinner(
            infoEnabled: true, senderOrigin: CI, interactiveCapability: true,
            ansiCapability: true, outputRedirected: false, userInteractive: true));
    }

    [TestMethod]
    public void ShouldUseLiveSpinner_NonInteractiveTerminal_ReturnsFalse()
    {
        Assert.IsFalse(ProgressDisplay.ShouldUseLiveSpinner(
            infoEnabled: true, senderOrigin: Direct, interactiveCapability: false,
            ansiCapability: true, outputRedirected: false, userInteractive: true));
    }

    [TestMethod]
    public void ShouldUseLiveSpinner_NoAnsiCapability_ReturnsFalse()
    {
        Assert.IsFalse(ProgressDisplay.ShouldUseLiveSpinner(
            infoEnabled: true, senderOrigin: Direct, interactiveCapability: true,
            ansiCapability: false, outputRedirected: false, userInteractive: true));
    }

    [TestMethod]
    public void ShouldUseLiveSpinner_OutputRedirected_ReturnsFalse()
    {
        Assert.IsFalse(ProgressDisplay.ShouldUseLiveSpinner(
            infoEnabled: true, senderOrigin: Direct, interactiveCapability: true,
            ansiCapability: true, outputRedirected: true, userInteractive: true));
    }

    [TestMethod]
    public void ShouldUseLiveSpinner_AllCapableAndInteractiveUser_ReturnsTrue()
    {
        Assert.IsTrue(ProgressDisplay.ShouldUseLiveSpinner(
            infoEnabled: true, senderOrigin: Direct, interactiveCapability: true,
            ansiCapability: true, outputRedirected: false, userInteractive: true));
    }

    [TestMethod]
    public void ShouldUseLiveSpinner_AllCapableButNonInteractiveUser_ReturnsFalse()
    {
        Assert.IsFalse(ProgressDisplay.ShouldUseLiveSpinner(
            infoEnabled: true, senderOrigin: Direct, interactiveCapability: true,
            ansiCapability: true, outputRedirected: false, userInteractive: false));
    }

    [TestMethod]
    public void ShouldUseLiveSpinner_ConsoleOverload_SuppressedLogger_ReturnsFalse()
    {
        // NullLogger reports info as disabled, so the console-driven overload returns false.
        var console = new TestConsole();
        Assert.IsFalse(ProgressDisplay.ShouldUseLiveSpinner(console, NullLogger.Instance));
    }
}
