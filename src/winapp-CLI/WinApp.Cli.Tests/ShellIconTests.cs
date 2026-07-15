// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Drawing;
using System.Runtime.InteropServices;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class ShellIconTests
{
    [TestMethod]
    public void GetJumboIcon_EmptyPath_DoesNotThrow()
    {
        // An empty path is resolved by the shell to a generic icon on a normal desktop, but may
        // fail on a constrained host. Either way the helper must never surface an exception.
        Icon? icon = ShellIcon.GetJumboIcon(string.Empty);
        icon?.Dispose();
    }

    [TestMethod]
    public void GetJumboIcon_NonexistentPath_ReturnsNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-icon-{Guid.NewGuid():N}.exe");
        Assert.IsNull(ShellIcon.GetJumboIcon(missing));
    }

    [TestMethod]
    public void GetJumboIcon_RealExecutable_ReturnsIconOrNullWithoutThrowing()
    {
        // Exercises the full shell image-list path against a real, icon-bearing executable. The
        // result depends on the host's shell availability, so we only require that it never throws
        // and that any returned icon is a usable, disposable handle.
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (!File.Exists(exe))
        {
            exe = Environment.ProcessPath!;
        }

        Icon? icon = ShellIcon.GetJumboIcon(exe);
        try
        {
            if (icon is not null)
            {
                Assert.IsTrue(icon.Width > 0 && icon.Height > 0, "A resolved icon must have positive dimensions.");
            }
        }
        finally
        {
            icon?.Dispose();
        }
    }

    [TestMethod]
    public void GetJumboIconCore_NoSystemIcon_ReturnsNullWithoutQueryingImageList()
    {
        var imageListQueried = false;

        var icon = ShellIcon.GetJumboIconCore(
            "irrelevant",
            _ => null,
            _ => { imageListQueried = true; return SystemIcons.Application; });

        Assert.IsNull(icon, "A null system icon index must short-circuit to null.");
        Assert.IsFalse(imageListQueried, "The image list must not be queried when there is no system icon.");
    }

    [TestMethod]
    public void GetJumboIconCore_ResolvedIndex_MaterializesIconForThatIndex()
    {
        int? seenIndex = null;

        var icon = ShellIcon.GetJumboIconCore(
            "irrelevant",
            _ => 7,
            index => { seenIndex = index; return SystemIcons.Application; });

        Assert.AreEqual(7, seenIndex, "The resolved system icon index must be forwarded to the image-list resolver.");
        Assert.AreSame(SystemIcons.Application, icon);
    }

    [TestMethod]
    public void GetJumboIconCore_SystemIndexResolverThrows_ReturnsNull()
    {
        var icon = ShellIcon.GetJumboIconCore(
            "irrelevant",
            _ => throw new InvalidOperationException("shell failure"),
            _ => SystemIcons.Application);

        Assert.IsNull(icon, "Exceptions from the native resolvers must be swallowed as null.");
    }

    [TestMethod]
    public void GetJumboIconCore_ImageListResolverThrows_ReturnsNull()
    {
        var icon = ShellIcon.GetJumboIconCore(
            "irrelevant",
            _ => 3,
            _ => throw new COMException("image list failure"));

        Assert.IsNull(icon, "Exceptions while materializing the icon must be swallowed as null.");
    }
}
