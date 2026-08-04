// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;
using static WinApp.Cli.Services.CertificateService;

namespace WinApp.Cli.Tests;

/// <summary>
/// Configurable fake <see cref="ICertificateService"/> for command tests that need deterministic
/// signing outcomes (success or a specific exception type) without invoking signtool or touching
/// the real certificate store. Records the arguments the command forwarded so delegation can be
/// asserted.
/// </summary>
internal sealed class FakeCertificateService : ICertificateService
{
    public Exception? SignException { get; set; }
    public FileInfo? LastSignedFile { get; private set; }
    public FileInfo? LastCertificatePath { get; private set; }
    public string? LastPassword { get; private set; }
    public string? LastTimestampUrl { get; private set; }
    public bool SignWasCalled { get; private set; }

    public Task SignFileAsync(FileInfo filePath, FileInfo certificatePath, TaskContext taskContext, string? password = "password", string? timestampUrl = null, CancellationToken cancellationToken = default)
    {
        SignWasCalled = true;
        LastSignedFile = filePath;
        LastCertificatePath = certificatePath;
        LastPassword = password;
        LastTimestampUrl = timestampUrl;
        if (SignException != null)
        {
            throw SignException;
        }
        return Task.CompletedTask;
    }

    public Task<CertificateResult> GenerateDevCertificateWithInferenceAsync(FileInfo outputPath, TaskContext taskContext, string? explicitPublisher = null, FileInfo? manifestPath = null, string password = "password", int validDays = 365, bool updateGitignore = true, bool install = false, bool exportCer = false, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<CertificateResult> GenerateDevCertificateAsync(string publisher, FileInfo outputPath, TaskContext taskContext, string password = "password", int validDays = 365, bool exportCer = false, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public bool InstallCertificate(FileInfo certPath, string password, bool force, TaskContext taskContext)
        => throw new NotSupportedException();
}
