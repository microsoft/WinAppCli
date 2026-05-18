// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

// `node` verb in the .NET CLI tree. Hosts Node.js / Electron-specific
// sub-commands that need real CLI work (currently `jsbindings`).
// Wrapper-only commands (`create-addon`, `add-electron-debug-identity`,
// `clear-electron-debug-identity`) are implemented in the npm shim and
// never reach this command — the shim intercepts them locally.
internal class NodeCommand : Command, IShortDescription
{
    public string ShortDescription => "Node.js / Electron-specific commands (npm-only)";

    public NodeCommand(JsBindingsCommand jsBindingsCommand)
        : base("node", "Node.js / Electron-specific winapp commands. Only available when invoked via "
            + "the @microsoft/winappcli npm package (npx winapp node ...).")
    {
        Subcommands.Add(jsBindingsCommand);
    }
}
