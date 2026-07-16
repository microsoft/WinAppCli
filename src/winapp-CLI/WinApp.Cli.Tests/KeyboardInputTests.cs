// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class KeyboardInputTests
{
    [TestInitialize]
    public void Initialize() => ResetSeams();

    [TestCleanup]
    public void Cleanup() => KeyboardInput.ResetNativeSeams();

    [TestMethod]
    public void BuildKeyLParam_ComposesScanExtendedSysAndKeyUpBits()
    {
        var down = KeyboardInput.BuildKeyLParam(isSys: true, keyUp: false, extended: true, scan: 0x1Du);
        Assert.AreEqual(1u | (0x1Du << 16) | (1u << 24) | (1u << 29), down);

        var up = KeyboardInput.BuildKeyLParam(isSys: false, keyUp: true, extended: false, scan: 0x2Eu);
        Assert.AreEqual(1u | (0x2Eu << 16) | (1u << 30) | (1u << 31), up);
    }

    [TestMethod]
    public void IsExtended_RecognizesWindowsMenuKeysOnly()
    {
        Assert.IsTrue(KeyboardInput.IsExtended(0x5B));
        Assert.IsTrue(KeyboardInput.IsExtended(0x5C));
        Assert.IsTrue(KeyboardInput.IsExtended(0x5D));
        Assert.IsFalse(KeyboardInput.IsExtended(0x41));
    }

    [TestMethod]
    public void BuildSendInputBatch_ChordOrdersModifierDownKeyDownKeyUpModifierUp()
    {
        var inputs = KeyboardInput.BuildSendInputBatch([
            new KeyChord([0x11, 0x5B], 0x2E, Extended: true)
        ], _ => 0);

        Assert.AreEqual(6, inputs.Length);
        AssertKey(inputs[0], 0x11, (KEYBD_EVENT_FLAGS)0);
        AssertKey(inputs[1], 0x5B, KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY);
        AssertKey(inputs[2], 0x2E, KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY);
        AssertKey(inputs[3], 0x2E, KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY | KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
        AssertKey(inputs[4], 0x5B, KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY | KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
        AssertKey(inputs[5], 0x11, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
    }

    [TestMethod]
    public void BuildSendInputBatch_TextUsesMappableShiftAndUnicodeFallbackBranches()
    {
        short Scan(char ch) => ch switch
        {
            'a' => 0x41,
            'A' => 0x0141,
            '€' => unchecked((short)0x0645),
            _ => -1
        };

        var inputs = KeyboardInput.BuildSendInputBatch([new TextInput("aA€☃")], Scan);

        Assert.AreEqual(10, inputs.Length);
        AssertKey(inputs[0], 0x41, (KEYBD_EVENT_FLAGS)0);
        AssertKey(inputs[1], 0x41, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
        AssertKey(inputs[2], 0x10, (KEYBD_EVENT_FLAGS)0);
        AssertKey(inputs[3], 0x41, (KEYBD_EVENT_FLAGS)0);
        AssertKey(inputs[4], 0x41, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
        AssertKey(inputs[5], 0x10, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
        AssertUnicode(inputs[6], '€', keyUp: false);
        AssertUnicode(inputs[7], '€', keyUp: true);
        AssertUnicode(inputs[8], '☃', keyUp: false);
        AssertUnicode(inputs[9], '☃', keyUp: true);
    }

    [TestMethod]
    public void Send_PostMessage_EmitsExactKeyAndCharMessages()
    {
        var posted = new List<(uint Message, nuint WParam, nint LParam)>();
        var sleeps = new List<int>();
        KeyboardInput.s_mapVirtualKey = vk => vk + 1u;
        KeyboardInput.s_sleep = sleeps.Add;
        KeyboardInput.s_postMessage = (_, message, wParam, lParam) => posted.Add((message, wParam.Value, lParam.Value));

        KeyboardInput.Send(123, [new KeyChord([0x12], 0x73, Extended: false), new TextInput("x")], KeyTransport.PostMessage);

        Assert.AreEqual(5, posted.Count);
        Assert.AreEqual(PInvoke.WM_KEYDOWN, posted[0].Message);
        Assert.AreEqual((nuint)0x12, posted[0].WParam);
        Assert.AreEqual((nint)(1u | (0x13u << 16)), posted[0].LParam);
        Assert.AreEqual(PInvoke.WM_SYSKEYDOWN, posted[1].Message);
        Assert.AreEqual((nuint)0x73, posted[1].WParam);
        Assert.AreEqual((nint)(int)(1u | (0x74u << 16) | (1u << 29)), posted[1].LParam);
        Assert.AreEqual(PInvoke.WM_SYSKEYUP, posted[2].Message);
        Assert.AreEqual(PInvoke.WM_KEYUP, posted[3].Message);
        Assert.AreEqual(PInvoke.WM_CHAR, posted[4].Message);
        Assert.AreEqual((nuint)'x', posted[4].WParam);
        Assert.AreEqual(2, sleeps.Count);
        Assert.AreEqual(5, sleeps[0]);
        Assert.AreEqual(5, sleeps[1]);
    }

    [TestMethod]
    public void Send_PostMessage_RequiresTargetWindow()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            KeyboardInput.Send(0, [new TextInput("x")], KeyTransport.PostMessage));
        StringAssert.Contains(ex.Message, "requires a target window");
    }

    [TestMethod]
    public void Send_UnknownTransportThrows()
    {
        var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            KeyboardInput.Send(1, [], (KeyTransport)99));
        Assert.AreEqual("transport", ex.ParamName);
    }

    [TestMethod]
    public void Send_SendInput_EmptyBatchDoesNotInvokeNativeSeam()
    {
        var calls = 0;
        KeyboardInput.s_sendInput = inputs => { calls++; return (uint)inputs.Length; };

        KeyboardInput.Send(0, [], KeyTransport.SendInput);

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void Send_SendInput_InvokesSeamWithExpectedBatch()
    {
        INPUT[]? observed = null;
        KeyboardInput.s_sendInput = inputs => { observed = inputs; return (uint)inputs.Length; };

        KeyboardInput.Send(0, [new KeyChord([], 0x41, Extended: false)], KeyTransport.SendInput);

        Assert.IsNotNull(observed);
        Assert.AreEqual(2, observed.Length);
        AssertKey(observed[0], 0x41, (KEYBD_EVENT_FLAGS)0);
        AssertKey(observed[1], 0x41, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
    }

    [TestMethod]
    public void Send_SendInput_ShortWriteReleasesPressedKeysInReverseOrder()
    {
        var batches = new List<INPUT[]>();
        KeyboardInput.s_foregroundWindowIsNull = () => false;
        KeyboardInput.s_sendInput = inputs =>
        {
            batches.Add(inputs.ToArray());
            return batches.Count == 1 ? 1u : (uint)inputs.Length;
        };

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            KeyboardInput.Send(0, [new KeyChord([0x11], 0x41, Extended: false)], KeyTransport.SendInput));

        StringAssert.Contains(ex.Message, "partially applied");
        Assert.AreEqual(2, batches.Count);
        Assert.AreEqual(4, batches[0].Length);
        Assert.AreEqual(2, batches[1].Length);
        AssertKey(batches[1][0], 0x41, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
        AssertKey(batches[1][1], 0x11, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
    }

    [TestMethod]
    [DataRow(true, "no interactive desktop")]
    [DataRow(false, "higher integrity level")]
    public void Send_SendInput_ZeroWriteReportsPreciseReason(bool noForeground, string expected)
    {
        KeyboardInput.s_foregroundWindowIsNull = () => noForeground;
        KeyboardInput.s_sendInput = inputs => 0;

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            KeyboardInput.Send(0, [new KeyChord([], 0x41, Extended: false)], KeyTransport.SendInput));

        StringAssert.Contains(ex.Message, expected);
    }

    [TestMethod]
    public void ReleaseHeldKeys_DoesNothingWhenNoDownKeyboardEvents()
    {
        var calls = 0;
        KeyboardInput.s_sendInput = inputs => { calls++; return (uint)inputs.Length; };
        var mouse = new INPUT { type = INPUT_TYPE.INPUT_MOUSE };
        var keyUp = KeyboardInput.KeyEvent(0x41, extended: false, keyUp: true);

        KeyboardInput.ReleaseHeldKeys([mouse, keyUp]);

        Assert.AreEqual(0, calls);
    }

    private static void AssertKey(INPUT input, ushort vk, KEYBD_EVENT_FLAGS flags)
    {
        Assert.AreEqual(INPUT_TYPE.INPUT_KEYBOARD, input.type);
        Assert.AreEqual((VIRTUAL_KEY)vk, input.Anonymous.ki.wVk);
        Assert.AreEqual(flags, input.Anonymous.ki.dwFlags);
    }

    private static void AssertUnicode(INPUT input, char ch, bool keyUp)
    {
        Assert.AreEqual(INPUT_TYPE.INPUT_KEYBOARD, input.type);
        Assert.AreEqual((VIRTUAL_KEY)0, input.Anonymous.ki.wVk);
        Assert.AreEqual(ch, input.Anonymous.ki.wScan);
        var expected = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE | (keyUp ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0);
        Assert.AreEqual(expected, input.Anonymous.ki.dwFlags);
    }

    [TestMethod]
    public void RealKeyboardInput_DelegatesToKeyboardInput()
    {
        var sent = new List<INPUT[]>();
        KeyboardInput.s_sendInput = inputs => { sent.Add(inputs.ToArray()); return (uint)inputs.Length; };

        new RealKeyboardInput().Send(0, [new KeyChord([], 0x41, Extended: false)], KeyTransport.SendInput);

        Assert.AreEqual(1, sent.Count,
            "RealKeyboardInput.Send must delegate to KeyboardInput.Send, which batches the chord into one SendInput seam call.");
    }

    private static void ResetSeams()
    {
        KeyboardInput.s_sendInput = inputs => (uint)inputs.Length;
        KeyboardInput.s_postMessage = (_, _, _, _) => { };
        KeyboardInput.s_vkKeyScan = PInvoke.VkKeyScan;
        KeyboardInput.s_mapVirtualKey = vk => PInvoke.MapVirtualKey(vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC);
        KeyboardInput.s_foregroundWindowIsNull = () => false;
        KeyboardInput.s_sleep = _ => { };
    }
}


