// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.AccessControl;
using System.Security.Principal;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

internal interface IWindowsSandboxMutationLock
{
    IDisposable Acquire(CancellationToken cancellationToken = default);
}

internal sealed class WindowsSandboxMutationLock : IWindowsSandboxMutationLock
{
    internal const string DefaultNamePrefix =
        @"Global\Microsoft.WinApp.ExecutionTarget.windows-sandbox-default.";

    private readonly string _name;
    private readonly SecurityIdentifier _userSid;

    public WindowsSandboxMutationLock() : this(GetCurrentUserSid())
    {
    }

    private WindowsSandboxMutationLock(SecurityIdentifier userSid)
        : this(DefaultNamePrefix + userSid.Value, userSid)
    {
    }

    internal WindowsSandboxMutationLock(string name) : this(name, GetCurrentUserSid())
    {
    }

    private WindowsSandboxMutationLock(string name, SecurityIdentifier userSid)
    {
        _name = name;
        _userSid = userSid;
    }

    internal string Name => _name;

    public IDisposable Acquire(CancellationToken cancellationToken = default)
    {
        var security = new MutexSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new MutexAccessRule(
            _userSid,
            MutexRights.FullControl,
            AccessControlType.Allow));
        var mutex = MutexAcl.Create(
            initiallyOwned: false,
            _name,
            out _,
            security);
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

    private static SecurityIdentifier GetCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.User
            ?? throw new InvalidOperationException("The current Windows user does not have a security identifier.");
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
