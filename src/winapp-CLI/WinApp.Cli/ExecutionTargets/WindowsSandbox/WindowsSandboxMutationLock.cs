// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

internal interface IWindowsSandboxMutationLock
{
    IDisposable Acquire(CancellationToken cancellationToken = default);
}

internal sealed class WindowsSandboxMutationLock : IWindowsSandboxMutationLock
{
    internal const string DefaultName =
        @"Local\Microsoft.WinApp.ExecutionTarget.windows-sandbox-default";

    private readonly string _name;

    public WindowsSandboxMutationLock() : this(DefaultName)
    {
    }

    internal WindowsSandboxMutationLock(string name)
    {
        _name = name;
    }

    public IDisposable Acquire(CancellationToken cancellationToken = default)
    {
        var mutex = new Mutex(initiallyOwned: false, _name);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (mutex.WaitOne(TimeSpan.FromMilliseconds(100)))
                    {
                        return new Lease(mutex);
                    }
                }
                catch (AbandonedMutexException)
                {
                    return new Lease(mutex);
                }
            }
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private sealed class Lease(Mutex mutex) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            mutex.ReleaseMutex();
            mutex.Dispose();
            _disposed = true;
        }
    }
}
