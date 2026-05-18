// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

// `node jsbindings` verb. Hosts subcommands that operate on the
// jsBindings: block of winapp.yaml — `add` (mutates yaml + codegen) and
// `generate` (read-only codegen).
internal class JsBindingsCommand : Command, IShortDescription
{
    public string ShortDescription => "Manage JS/TS WinRT bindings (npm-only)";

    public JsBindingsCommand(
        AddJsBindingsCommand addJsBindingsCommand,
        GenerateJsBindingsCommand generateJsBindingsCommand)
        : base("jsbindings", "Manage JS/TS WinRT bindings for an existing workspace. "
            + "'add' mutates winapp.yaml + runs codegen; 'generate' just runs codegen "
            + "against the existing yaml. Only available via the @microsoft/winappcli npm package.")
    {
        // Kebab-case alias — matches the --js-bindings flag name in init.
        Aliases.Add("js-bindings");
        Subcommands.Add(addJsBindingsCommand);
        Subcommands.Add(generateJsBindingsCommand);
    }
}
