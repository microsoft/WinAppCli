// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

internal record CreateMsixPackageResult(FileInfo MsixPath, bool Signed);
