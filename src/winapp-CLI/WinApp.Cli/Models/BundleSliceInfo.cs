// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

/// <summary>
/// Metadata about a single architecture slice within a bundle.
/// </summary>
internal record BundleSliceInfo(DirectoryInfo InputFolder, string Architecture, FileInfo IntermediateMsixPath);
