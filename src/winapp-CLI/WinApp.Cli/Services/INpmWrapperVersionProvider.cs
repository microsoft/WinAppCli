// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

// Pinned version from the @microsoft/winappcli npm wrapper. dynwinrt and
// dynwinrt-codegen ship in lockstep.
public interface INpmWrapperVersionProvider
{
    string DynWinrtVersion { get; }
    string DynWinrtCodegenVersion { get; }
}
