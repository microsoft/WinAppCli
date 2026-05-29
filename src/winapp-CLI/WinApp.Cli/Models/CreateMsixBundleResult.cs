// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

internal record CreateMsixBundleResult(FileInfo BundlePath, bool Signed, IReadOnlyList<BundleSliceInfo> Slices);
