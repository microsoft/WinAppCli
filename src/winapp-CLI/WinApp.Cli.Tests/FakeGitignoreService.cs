// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake <see cref="IGitignoreService"/> that records requests and returns a
/// configurable "did I change the file" result, so callers can be tested without
/// touching a real .gitignore.
/// </summary>
internal sealed class FakeGitignoreService : IGitignoreService
{
    public List<(DirectoryInfo Directory, string FileName)> CertificateRequests { get; } = [];
    public bool CertificateResult { get; set; } = true;

    public Task<bool> AddWinAppFolderToGitIgnoreAsync(DirectoryInfo projectDirectory, TaskContext taskContext, CancellationToken cancellationToken)
        => Task.FromResult(true);

    public Task<bool> AddCertificateToGitignoreAsync(DirectoryInfo projectDirectory, string certificateFileName, TaskContext taskContext, CancellationToken cancellationToken)
    {
        CertificateRequests.Add((projectDirectory, certificateFileName));
        return Task.FromResult(CertificateResult);
    }
}
