// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Diagnostics.Telemetry;
using Microsoft.Diagnostics.Telemetry.Internal;
using System.Diagnostics.Tracing;

namespace WinApp.Cli.Telemetry.Events;

[EventData]
internal class ExceptionThrownEvent : EventBase
{
    internal ExceptionThrownEvent(string action, Exception e, Func<string, string> replaceSensitiveStrings)
    {
        Action = action;
        Name = e.GetType().Name;
        StackTrace = e.StackTrace;
        InnerName = e.InnerException?.GetType().Name;
        Message = replaceSensitiveStrings(e.Message);

        var innerMessage = e.InnerException?.Message;
        InnerMessage = innerMessage != null ? replaceSensitiveStrings(innerMessage) : null;

        var sb = new System.Text.StringBuilder();
        var innerException = e.InnerException;
        while (innerException != null)
        {
            sb.Append(innerException.StackTrace);
            sb.AppendLine();
            sb.AppendLine();
            innerException = innerException.InnerException;
        }

        InnerStackTrace = sb.ToString();
    }

    public string Action { get; private set; }

    public string Name { get; private set; }

    public string? StackTrace { get; private set; }

    public string? InnerName { get; private set; }

    public string? InnerMessage { get; private set; }

    public string InnerStackTrace { get; private set; }

    public string Message { get; private set; }

    public override PartA_PrivTags PartA_PrivTags => PrivTags.ProductAndServicePerformance;

    public override void ReplaceSensitiveStrings(Func<string, string> replaceSensitiveStrings)
    {
        Action = replaceSensitiveStrings(Action);
        Message = replaceSensitiveStrings(Message);
        if (InnerMessage != null)
        {
            InnerMessage = replaceSensitiveStrings(InnerMessage);
        }
    }
}
