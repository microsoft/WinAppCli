// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32.UI.Input.Pointer;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Unit tests that verify the frame sequences produced by <see cref="PointerInput"/> without a live
/// Windows desktop. They call the internal <c>InjectTouchStroke</c> / <c>InjectPenStroke</c> methods
/// directly with a recording delegate so the P/Invoke path is never exercised.
/// </summary>
[TestClass]
public class PointerInputFrameTests
{
    // -------------------------------------------------------------------------
    // HIGH 1 — Long-press emits periodic UPDATE frames during the hold
    // -------------------------------------------------------------------------

    [TestMethod]
    public void InjectTouchStroke_LongPress_EmitsUpdateFramesBetweenDownAndUp()
    {
        // Use a small holdMs so the test completes quickly; two full intervals → ≥2 UPDATE frames.
        const int holdMs = 85; // two full 40ms intervals + 5ms remainder → 3 UPDATE frames
        var frames = new List<(POINTER_FLAGS Flags, int X, int Y)>();

        PointerInput.TouchSender recorder = contacts =>
        {
            foreach (var c in contacts)
            {
                frames.Add((c.pointerInfo.pointerFlags, c.pointerInfo.ptPixelLocation.X, c.pointerInfo.ptPixelLocation.Y));
            }
        };

        var paths = new List<IReadOnlyList<PointerPoint>>
        {
            new List<PointerPoint> { new PointerPoint(100, 200) } // single-point = long-press (no glide)
        };

        PointerInput.InjectTouchStroke(paths, holdMs, durationMs: 0, recorder);

        // Must have at least 3 frames total (DOWN, ≥1 UPDATE, UP).
        Assert.IsTrue(frames.Count >= 3, $"Expected ≥3 frames, got {frames.Count}");

        // First frame must be DOWN.
        Assert.IsTrue(frames[0].Flags.HasFlag(POINTER_FLAGS.POINTER_FLAG_DOWN),
            "First frame must be POINTER_FLAG_DOWN");

        // Last frame must be UP.
        Assert.IsTrue(frames[^1].Flags.HasFlag(POINTER_FLAGS.POINTER_FLAG_UP),
            "Last frame must be POINTER_FLAG_UP");

        // Intermediate frames must all be UPDATE + INRANGE + INCONTACT at the pressed coordinates.
        var updateFrames = frames.Skip(1).SkipLast(1).ToList();
        Assert.IsTrue(updateFrames.Count > 0, "Long-press must emit at least one UPDATE frame during hold");

        foreach (var f in updateFrames)
        {
            Assert.IsTrue(f.Flags.HasFlag(POINTER_FLAGS.POINTER_FLAG_UPDATE),
                "Hold frames must carry POINTER_FLAG_UPDATE");
            Assert.IsTrue(f.Flags.HasFlag(POINTER_FLAGS.POINTER_FLAG_INRANGE),
                "Hold frames must carry POINTER_FLAG_INRANGE");
            Assert.IsTrue(f.Flags.HasFlag(POINTER_FLAGS.POINTER_FLAG_INCONTACT),
                "Hold frames must carry POINTER_FLAG_INCONTACT");
            Assert.AreEqual(100, f.X, "Hold UPDATE frames must be at the pressed X coordinate");
            Assert.AreEqual(200, f.Y, "Hold UPDATE frames must be at the pressed Y coordinate");
        }
    }

    [TestMethod]
    public void InjectTouchStroke_MultiContact_LongPress_EmitsUpdateFramesForAllContacts()
    {
        // Two fingers on a long-press: both contacts must receive UPDATE frames during the hold.
        const int holdMs = 45; // one interval (40ms) + 5ms → 2 UPDATE frames per finger
        var frameGroups = new List<POINTER_FLAGS[]>(); // each element is the flags array for one send() call

        PointerInput.TouchSender recorder = contacts =>
        {
            frameGroups.Add(contacts.Select(c => c.pointerInfo.pointerFlags).ToArray());
        };

        var paths = new List<IReadOnlyList<PointerPoint>>
        {
            new List<PointerPoint> { new PointerPoint(100, 200) },
            new List<PointerPoint> { new PointerPoint(124, 200) }, // finger 2 offset
        };

        PointerInput.InjectTouchStroke(paths, holdMs, durationMs: 0, recorder);

        // Each frame group must have exactly 2 contacts.
        foreach (var group in frameGroups)
        {
            Assert.AreEqual(2, group.Length, "Each sent frame must carry all active contacts");
        }

        // Must have intermediate UPDATE frames (not just DOWN + UP).
        var updateGroups = frameGroups.Skip(1).SkipLast(1).ToList();
        Assert.IsTrue(updateGroups.Count > 0, "Multi-finger long-press must emit UPDATE frames during hold");
    }

    [TestMethod]
    public void InjectTouchStroke_Tap_EmitsNoUpdateFramesDuringHold()
    {
        // holdMs = 0 → plain tap: only DOWN + UP, zero interim UPDATE frames.
        var frames = new List<POINTER_FLAGS>();

        PointerInput.TouchSender recorder = contacts =>
        {
            foreach (var c in contacts)
            {
                frames.Add(c.pointerInfo.pointerFlags);
            }
        };

        var paths = new List<IReadOnlyList<PointerPoint>>
        {
            new List<PointerPoint> { new PointerPoint(100, 200) }
        };

        PointerInput.InjectTouchStroke(paths, holdMs: 0, durationMs: 0, recorder);

        Assert.AreEqual(2, frames.Count,
            "A plain tap (holdMs=0, single-point path) must produce exactly DOWN + UP — no interim UPDATE frames");
        Assert.IsTrue(frames[0].HasFlag(POINTER_FLAGS.POINTER_FLAG_DOWN), "Frame 0 must be DOWN");
        Assert.IsTrue(frames[1].HasFlag(POINTER_FLAGS.POINTER_FLAG_UP), "Frame 1 must be UP");
    }

    // -------------------------------------------------------------------------
    // HIGH 2 — Pen --duration-ms is the stroke travel time, not dwell-at-end
    // -------------------------------------------------------------------------

    [TestMethod]
    public void InjectPenStroke_WithDurationMs_EmitsInterpolatedUpdateFrames()
    {
        // 2-point path + durationMs=200 → GlideSteps(20) UPDATE frames between DOWN and UP.
        var frames = new List<(int X, int Y, uint Pressure, POINTER_FLAGS Flags)>();

        PointerInput.PenFrameSender recorder = (x, y, p, flags) => frames.Add((x, y, p, flags));

        var path = new List<PointerPoint>
        {
            new PointerPoint(100, 100),
            new PointerPoint(200, 200),
        };

        PointerInput.InjectPenStroke(path, contactPressure: 512, durationMs: 200, recorder);

        // Must have more than just DOWN + UP.
        Assert.IsTrue(frames.Count > 2,
            $"durationMs stroke must emit UPDATE frames; got {frames.Count} total frames");

        Assert.IsTrue(frames[0].Flags.HasFlag(POINTER_FLAGS.POINTER_FLAG_DOWN), "First frame must be DOWN");
        Assert.IsTrue(frames[^1].Flags.HasFlag(POINTER_FLAGS.POINTER_FLAG_UP), "Last frame must be UP");

        var updateFrames = frames.Skip(1).SkipLast(1).ToList();
        Assert.IsTrue(updateFrames.Count > 1,
            "Must emit multiple UPDATE frames (not a single teleport) when durationMs is set");

        // All intermediate frames must be UPDATE+INRANGE+INCONTACT.
        foreach (var f in updateFrames)
        {
            Assert.IsTrue(f.Flags.HasFlag(POINTER_FLAGS.POINTER_FLAG_UPDATE),
                "Glide frames must carry POINTER_FLAG_UPDATE");
            Assert.IsTrue(f.Flags.HasFlag(POINTER_FLAGS.POINTER_FLAG_INRANGE));
            Assert.IsTrue(f.Flags.HasFlag(POINTER_FLAGS.POINTER_FLAG_INCONTACT));
        }

        // X and Y positions of UPDATE frames must progress monotonically from (100,100) toward (200,200).
        for (int i = 1; i < updateFrames.Count; i++)
        {
            Assert.IsTrue(updateFrames[i].X >= updateFrames[i - 1].X,
                $"X must not decrease between consecutive UPDATE frames ({updateFrames[i - 1].X} → {updateFrames[i].X})");
            Assert.IsTrue(updateFrames[i].Y >= updateFrames[i - 1].Y,
                $"Y must not decrease between consecutive UPDATE frames ({updateFrames[i - 1].Y} → {updateFrames[i].Y})");
        }

        // The last UPDATE frame must reach or be very close to the destination.
        var lastUpdate = updateFrames[^1];
        Assert.AreEqual(200, lastUpdate.X, "Last UPDATE frame must reach destination X");
        Assert.AreEqual(200, lastUpdate.Y, "Last UPDATE frame must reach destination Y");
    }

    [TestMethod]
    public void InjectPenStroke_WithDurationMs_TotalSleepApproximatesDurationMs()
    {
        // Injected clock: fakeNow advances only via fakeSleep so total recorded sleep == scheduled ms.
        // With 1 segment × GlideSteps=20 frames and durationMs=200, each frame targets 10ms apart:
        // total sleep must be exactly 200ms (no Math.Max(1,…) inflation, no trailing dwell beyond schedule).
        long fakeNow = 0;
        var sleeps = new List<int>();
        void fakeSleep(int ms) { sleeps.Add(ms); fakeNow += ms; }
        long fakeNowMs() => fakeNow;

        var frames = new List<(POINTER_FLAGS Flags, int X, int Y)>();
        PointerInput.PenFrameSender recorder = (x, y, p, flags) => frames.Add((flags, x, y));

        var path = new List<PointerPoint>
        {
            new PointerPoint(0, 0),
            new PointerPoint(100, 0),
        };

        PointerInput.InjectPenStroke(path, contactPressure: 512, durationMs: 200, recorder, fakeSleep, fakeNowMs);

        // Frame count: DOWN + 20 UPDATE + UP = 22.
        Assert.AreEqual(22, frames.Count,
            $"1-segment stroke with durationMs>0 should produce DOWN + 20 UPDATE + UP = 22 frames; got {frames.Count}");

        // Total scheduled sleep must equal durationMs exactly (cumulative target timestamp approach).
        int totalSleep = sleeps.Sum();
        Assert.AreEqual(200, totalSleep,
            $"Total sleep must equal durationMs=200ms; got {totalSleep}ms across {sleeps.Count} intervals");
    }

    [TestMethod]
    public void InjectPenStroke_SmallDurationMs_NotInflatedToStepCount()
    {
        // With durationMs=1 and GlideSteps=20, the old Math.Max(1,…) code would sleep 1ms×20 = 20ms.
        // The cumulative-timestamp approach must yield total sleep ≈ 1ms regardless of step count.
        long fakeNow = 0;
        var sleeps = new List<int>();
        void fakeSleep(int ms) { sleeps.Add(ms); fakeNow += ms; }
        long fakeNowMs() => fakeNow;

        var frames = new List<(POINTER_FLAGS Flags, int X, int Y)>();
        PointerInput.PenFrameSender recorder = (x, y, p, flags) => frames.Add((flags, x, y));

        var path = new List<PointerPoint>
        {
            new PointerPoint(0, 0),
            new PointerPoint(100, 0),
        };

        PointerInput.InjectPenStroke(path, contactPressure: 512, durationMs: 1, recorder, fakeSleep, fakeNowMs);

        int totalSleep = sleeps.Sum();
        Assert.AreEqual(1, totalSleep,
            $"durationMs=1 must not be inflated by step count; expected total=1ms, got {totalSleep}ms. " +
            $"Old Math.Max(1,…) code would return ~{sleeps.Count}ms.");

        // Still must produce the correct frame sequence.
        Assert.AreEqual(22, frames.Count, "Frame count must still be DOWN + 20 UPDATE + UP = 22");
        Assert.AreEqual(100, frames[^2].X, "Last UPDATE frame must reach destination X=100");
    }

    [TestMethod]
    public void InjectPenStroke_WithDurationMs_NoTrailingDwellAfterEndpoint()
    {
        // The sleep schedule must end at or before the endpoint UPDATE frame; the UP frame must
        // follow immediately with no extra sleep (no trailing dwell beyond the scheduled duration).
        long fakeNow = 0;
        bool lastEventWasSleep = false;
        void fakeSleep(int ms) { fakeNow += ms; lastEventWasSleep = true; }
        long fakeNowMs() => fakeNow;

        var frames = new List<POINTER_FLAGS>();
        PointerInput.PenFrameSender recorder = (x, y, p, flags) =>
        {
            frames.Add(flags);
            lastEventWasSleep = false;
        };

        var path = new List<PointerPoint>
        {
            new PointerPoint(0, 0),
            new PointerPoint(100, 0),
        };

        PointerInput.InjectPenStroke(path, contactPressure: 512, durationMs: 200, recorder, fakeSleep, fakeNowMs);

        // The UP frame is the last event; no sleep should occur between the endpoint UPDATE and the UP.
        Assert.IsFalse(lastEventWasSleep,
            "No sleep should occur after the endpoint UPDATE frame; the UP frame must follow immediately");
        Assert.IsTrue(frames[^1].HasFlag(POINTER_FLAGS.POINTER_FLAG_UP), "Last frame must be UP");
    }

    [TestMethod]
    public void InjectPenStroke_MultiSegment_WithDurationMs_TotalSleepEqualsDurationMs()
    {
        // 3-point path (2 segments) with injected clock: total sleep must still equal durationMs exactly.
        long fakeNow = 0;
        var sleeps = new List<int>();
        void fakeSleep(int ms) { sleeps.Add(ms); fakeNow += ms; }
        long fakeNowMs() => fakeNow;

        var frames = new List<(POINTER_FLAGS Flags, int X, int Y)>();
        PointerInput.PenFrameSender recorder = (x, y, p, flags) => frames.Add((flags, x, y));

        var path = new List<PointerPoint>
        {
            new PointerPoint(0, 0),
            new PointerPoint(50, 0),
            new PointerPoint(100, 0),
        };

        PointerInput.InjectPenStroke(path, contactPressure: 512, durationMs: 400, recorder, fakeSleep, fakeNowMs);

        // 2 segments × 20 steps = 40 UPDATE frames; DOWN + 40 + UP = 42.
        Assert.AreEqual(42, frames.Count,
            $"2-segment stroke should produce DOWN + 40 UPDATE + UP = 42 frames; got {frames.Count}");

        int totalSleep = sleeps.Sum();
        Assert.AreEqual(400, totalSleep,
            $"Total sleep for 2-segment stroke must equal durationMs=400ms; got {totalSleep}ms");
    }

    [TestMethod]
    public void InjectPenStroke_ZeroDurationMs_FallsBackToOneUpdatePerWaypoint()
    {
        // durationMs=0 → old behavior: one UPDATE per intermediate waypoint, no interpolation.
        var frames = new List<(POINTER_FLAGS Flags, int X, int Y)>();
        PointerInput.PenFrameSender recorder = (x, y, p, flags) => frames.Add((flags, x, y));

        // 3-point path (2 segments).
        var path = new List<PointerPoint>
        {
            new PointerPoint(0, 0),
            new PointerPoint(50, 0),
            new PointerPoint(100, 0),
        };

        PointerInput.InjectPenStroke(path, contactPressure: 512, durationMs: 0, recorder);

        // durationMs=0 → 1 UPDATE per waypoint (2 waypoints) → DOWN + 2 UPDATE + UP = 4 frames.
        Assert.AreEqual(4, frames.Count,
            $"durationMs=0 should produce DOWN + 1 UPDATE per waypoint + UP; got {frames.Count}");
        Assert.IsTrue(frames[1].X == 50, "First UPDATE should be at waypoint[1] X=50");
        Assert.IsTrue(frames[2].X == 100, "Second UPDATE should be at waypoint[2] X=100");
    }

    // -------------------------------------------------------------------------
    // MEDIUM 1 — PointerRect.Contains exclusive-bounds fix
    // -------------------------------------------------------------------------

    [TestMethod]
    public void PointerRect_Contains_InsidePoint_IsTrue()
    {
        var rect = new PointerRect(0, 0, 800, 600);
        Assert.IsTrue(rect.Contains(new PointerPoint(0, 0)), "Top-left corner must be inside");
        Assert.IsTrue(rect.Contains(new PointerPoint(799, 599)), "One pixel from each exclusive edge must be inside");
        Assert.IsTrue(rect.Contains(new PointerPoint(400, 300)), "Centre must be inside");
    }

    [TestMethod]
    public void PointerRect_Contains_RightEdge_IsExclusive()
    {
        var rect = new PointerRect(0, 0, 800, 600);
        Assert.IsFalse(rect.Contains(new PointerPoint(800, 300)),
            "A point exactly on Right (800) must be OUTSIDE (exclusive bound)");
    }

    [TestMethod]
    public void PointerRect_Contains_BottomEdge_IsExclusive()
    {
        var rect = new PointerRect(0, 0, 800, 600);
        Assert.IsFalse(rect.Contains(new PointerPoint(400, 600)),
            "A point exactly on Bottom (600) must be OUTSIDE (exclusive bound)");
    }

    [TestMethod]
    public void PointerRect_Contains_LeftEdge_IsInclusive()
    {
        var rect = new PointerRect(100, 100, 800, 600);
        Assert.IsTrue(rect.Contains(new PointerPoint(100, 300)), "Left edge must be INSIDE (inclusive)");
        Assert.IsFalse(rect.Contains(new PointerPoint(99, 300)), "One pixel left of Left must be outside");
    }

    [TestMethod]
    public void PointerRect_Contains_TopEdge_IsInclusive()
    {
        var rect = new PointerRect(100, 100, 800, 600);
        Assert.IsTrue(rect.Contains(new PointerPoint(300, 100)), "Top edge must be INSIDE (inclusive)");
        Assert.IsFalse(rect.Contains(new PointerPoint(300, 99)), "One pixel above Top must be outside");
    }
}
