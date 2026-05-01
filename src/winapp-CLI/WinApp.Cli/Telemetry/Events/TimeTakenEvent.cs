// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Diagnostics.Telemetry;
using Microsoft.Diagnostics.Telemetry.Internal;
using System.Diagnostics.Tracing;

namespace WinApp.Cli.Telemetry.Events;

[EventData]
internal class TimeTakenEvent : EventBase
{
    internal TimeTakenEvent(string eventName, uint timeTakenMilliseconds)
    {
        EventName = eventName;
        TimeTakenMilliseconds = timeTakenMilliseconds;
    }

    public string EventName { get; private set; }

    public uint TimeTakenMilliseconds { get; private set; }

    public override PartA_PrivTags PartA_PrivTags => PrivTags.ProductAndServicePerformance;

    public override void ReplaceSensitiveStrings(Func<string, string> replaceSensitiveStrings)
    {
        EventName = replaceSensitiveStrings(EventName);
    }
}
