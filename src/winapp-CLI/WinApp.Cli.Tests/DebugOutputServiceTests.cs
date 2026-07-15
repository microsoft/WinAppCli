// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using WinApp.Cli.Services;
using Windows.Win32.System.Diagnostics.Debug;

namespace WinApp.Cli.Tests;

/// <summary>
/// Unit tests for the pure, static classification helpers of <see cref="DebugOutputService"/>:
/// exception-code naming and the framework-noise console filters. These are deterministic and have
/// no OS dependencies, so they run in the default parallel phase.
/// </summary>
[TestClass]
public sealed class DebugOutputServiceTests
{
    [TestMethod]
    [DataRow(0xC0000005u, "Access Violation")]
    [DataRow(0xC00000FDu, "Stack Overflow")]
    [DataRow(0xC0000094u, "Integer Division By Zero")]
    [DataRow(0xC0000017u, "No Memory")]
    [DataRow(0xC000001Du, "Illegal Instruction")]
    [DataRow(0xC0000025u, "Non-Continuable Exception")]
    [DataRow(0xC000008Cu, "Array Bounds Exceeded")]
    [DataRow(0xC0000135u, "DLL Not Found")]
    [DataRow(0xC0000142u, "DLL Initialization Failed")]
    [DataRow(0x80000003u, "Breakpoint")]
    [DataRow(0x80000004u, "Single Step")]
    [DataRow(0xE06D7363u, "C++ Exception")]
    [DataRow(0xE0434352u, "CLR Exception")]
    public void GetExceptionName_KnownCodes_ReturnMappedName(uint code, string expected)
    {
        Assert.AreEqual(expected, DebugOutputService.GetExceptionName(code));
    }

    [TestMethod]
    [DataRow(0x00000000u)]
    [DataRow(0xDEADBEEFu)]
    [DataRow(0x12345678u)]
    public void GetExceptionName_UnknownCode_ReturnsGenericException(uint code)
    {
        Assert.AreEqual("Exception", DebugOutputService.GetExceptionName(code));
    }

    [TestMethod]
    [DataRow("onecore\\com\\combase\\foo.cpp")]
    [DataRow("ONECOREUAP\\shell\\bar.cpp")]
    [DataRow("minkernel\\crts\\baz.c")]
    [DataRow("C:\\__w\\1\\s\\sdk\\inc\\thing.h")]
    public void IsFrameworkNoise_OsAndBuildPaths_AreNoise(string message)
    {
        Assert.IsTrue(DebugOutputService.IsFrameworkNoise(message));
    }

    [TestMethod]
    [DataRow("wil: ReturnHr(1) tid(4) 80070005")]
    [DataRow("LogHr(2) failed")]
    [DataRow("ReturnNt(3) status")]
    public void IsFrameworkNoise_WilTraceMarkers_AreNoise(string message)
    {
        Assert.IsTrue(DebugOutputService.IsFrameworkNoise(message));
    }

    [TestMethod]
    [DataRow("E_INVALIDARG occurred")]
    [DataRow("E_FAIL somewhere")]
    [DataRow("HRESULT: 0x80004005")]
    [DataRow("hr = 0x80070002")]
    public void IsFrameworkNoise_HresultNoise_AreNoise(string message)
    {
        Assert.IsTrue(DebugOutputService.IsFrameworkNoise(message));
    }

    [TestMethod]
    public void IsFrameworkNoise_DllTrace_IsNoise()
    {
        Assert.IsTrue(DebugOutputService.IsFrameworkNoise("Microsoft.UI.Xaml.dll!0x00001234 something"));
    }

    [TestMethod]
    [DataRow("Loading page data from server")]
    [DataRow("MyApp: user clicked button")]
    [DataRow("")]
    [DataRow("hello world")]
    public void IsFrameworkNoise_AppMessages_AreNotNoise(string message)
    {
        Assert.IsFalse(DebugOutputService.IsFrameworkNoise(message));
    }

    [TestMethod]
    [DataRow("Microsoft.UI.Xaml.dll!0xabcdef")]
    [DataRow("Microsoft.Windows.Foo.dll!Bar")]
    [DataRow("Microsoft.Web.WebView2.Core.dll!Baz")]
    [DataRow("Microsoft.WinUI.dll!Qux")]
    [DataRow("twinapi.appcore.dll!SomeFunc")]
    [DataRow("Windows.UI.Xaml.dll!Thing")]
    [DataRow("dxgi.dll!Present")]
    [DataRow("d3d11.dll!Draw")]
    [DataRow("d2d1.dll!Fill")]
    [DataRow("combase.dll!CoCreate")]
    [DataRow("oleaut32.dll!VariantClear")]
    [DataRow("ntdll.dll!RtlUserThreadStart")]
    [DataRow("kernelbase.dll!GetLastError")]
    [DataRow("kernel32.dll!CreateFileW")]
    [DataRow("WinAppRuntime.dll!Bootstrap")]
    [DataRow("MRM.dll!Lookup")]
    public void IsFrameworkDllTrace_KnownFrameworkDlls_AreTrace(string message)
    {
        Assert.IsTrue(DebugOutputService.IsFrameworkDllTrace(message));
    }

    [TestMethod]
    [DataRow("ab!cd", "bang before index 5")]
    [DataRow("hi!there", "short prefix")]
    public void IsFrameworkDllTrace_BangTooEarly_IsNotTrace(string message, string _)
    {
        Assert.IsFalse(DebugOutputService.IsFrameworkDllTrace(message));
    }

    [TestMethod]
    public void IsFrameworkDllTrace_NoBang_IsNotTrace()
    {
        Assert.IsFalse(DebugOutputService.IsFrameworkDllTrace("just a plain message with no bang"));
    }

    [TestMethod]
    public void IsFrameworkDllTrace_NotADll_IsNotTrace()
    {
        Assert.IsFalse(DebugOutputService.IsFrameworkDllTrace("SomeModule.exe!Function"));
    }

    [TestMethod]
    public void IsFrameworkDllTrace_UnknownAppDll_IsNotTrace()
    {
        // A .dll trace whose module is not in the framework allow-list is treated as app output.
        Assert.IsFalse(DebugOutputService.IsFrameworkDllTrace("Contoso.Widgets.dll!DoWork"));
    }

    // ---- M2: pure decisions previously declared uncoverable ----

    [TestMethod]
    public void GetContextFlags_MapsArchitectureToContextFlags()
    {
        // Both arms of the CONTEXT-flags switch are pure decisions, coverable off-host (the Arm64 arm is
        // unreachable at runtime on this x64 host but the mapping itself is not architecture-gated).
        Assert.AreEqual(CONTEXT_FLAGS.CONTEXT_FULL_ARM64, DebugOutputService.GetContextFlags(Architecture.Arm64));
        Assert.AreEqual(CONTEXT_FLAGS.CONTEXT_FULL_AMD64, DebugOutputService.GetContextFlags(Architecture.X64));
        Assert.AreEqual(CONTEXT_FLAGS.CONTEXT_FULL_AMD64, DebugOutputService.GetContextFlags(Architecture.X86));
        Assert.AreEqual(CONTEXT_FLAGS.CONTEXT_FULL_AMD64, DebugOutputService.GetContextFlags((Architecture)999));
    }

    [TestMethod]
    public void ReadExceptionParameters_ZeroParameters_ReturnsNull()
    {
        // NumberParameters == 0 -> the early-return null path (no allocation).
        var record = new EXCEPTION_RECORD { NumberParameters = 0 };
        Assert.IsNull(DebugOutputService.ReadExceptionParameters(record));
    }

    [TestMethod]
    public void ReadExceptionParameters_CopiesDefinedParameters()
    {
        // NumberParameters > 0 -> the defined leading elements are copied verbatim (e.g. a stowed
        // exception's array pointer + count), capped at the ExceptionInformation length.
        var record = new EXCEPTION_RECORD { NumberParameters = 2 };
        record.ExceptionInformation._0 = (nuint)0x1111;
        record.ExceptionInformation._1 = (nuint)0x2222;

        var result = DebugOutputService.ReadExceptionParameters(record);

        Assert.IsNotNull(result);
        Assert.AreEqual(2, result!.Length);
        Assert.AreEqual((nuint)0x1111, result[0]);
        Assert.AreEqual((nuint)0x2222, result[1]);
    }

    [TestMethod]
    public void ReadExceptionParameters_CountClampedToInformationLength()
    {
        // A bogus NumberParameters larger than the fixed ExceptionInformation buffer must be clamped to
        // the buffer length (15) rather than reading out of bounds.
        var record = new EXCEPTION_RECORD { NumberParameters = 99 };

        var result = DebugOutputService.ReadExceptionParameters(record);

        Assert.IsNotNull(result);
        Assert.AreEqual(15, result!.Length);
    }
}
