# Packaging and Package Identity for .NET apps with winapp CLI

Package identity has often been a pain point for developers looking to build apps that integrate with Windows APIs. Many modern Windows features, like push notifications or the AI APIs, are gated behind package identity. For Windows apps that are unpackaged by default (like .NET console or WPF applications), this meant wrestling with package manifests, build configurations, and certs to bring your app up to speed.

Now, with the [**winapp CLI**](https://github.com/microsoft/winappCli), you can quickly tackle the problem of package identity, both in the context of local running and debugging, and for packaging applications as MSIX for distribution.

The winapp CLI enables a whole host of development related features, but we'll be highlighting two key capabilities to start:

1. The winapp CLI integrates with existing dotnet tooling to enable you to test your application with package identity via `dotnet run`
2. The winapp CLI makes packaging applications as MSIX easy via `winapp pack`

<!-- image with cli first run visual -->


If you want to follow along with the examples in this post, install the CLI with winget:

```
winget install Microsoft.winappcli --source winget
```

## `dotnet run` with Package Identity

Once you have the winapp CLI installed, running your .NET app with identity is easy:

### 1. Initialize your project with winapp.

```
winapp init --use-defaults
```

The init command takes care of all the prerequisites for enabling identity:
- Ensures the `TargetFramework` specified in your `.csproj` is supported
- Add `Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools`, and `Microsoft.Windows.SDK.BuildTools.WinApp` NuGet package references to your `.csproj`
- Generates both a `Package.appxmanifest` and required asset files, placed in an `Assets` directory

If you want to more control over the init experience, run without the `--use-defaults` flag.

### 2. Debug with `dotnet run`

Once your project has been initialized with winapp, you can run your app as you would normally:

```
dotnet run
```

This will launch your app with package identity, allowing you to easily add and test Windows features within your app.

For more details on how exactly the winapp CLI works with dotnet under the hood, checkout the [Dotnet run support docs.](https://github.com/microsoft/winappCli/blob/main/docs/dotnet-run-support.md)

### 3. Adding an execution alias for console applications (optional)

If you are building a console application, and want output to remain in the current terminal window, you will need to add an execution alias:

1. Add the required alias to your `Package.appxmanifest` with winapp CLI:
```
winapp manifest add-alias
```
2. Tell your application to use the alias by opening your `.csproj` and adding this property to any `<PropertyGroup>`:

```
<WinAppRunUseExecutionAlias>true</WinAppRunUseExecutionAlias>
```

### That's it.

Those two steps (or three for console applications) are all that's required to run your application with identity locally.

Now what about packaging?

## Creating MSIX packages with `winapp pack`

The winapp CLI provides `pack` and `cert generate` commands to streamline the MSIX creation process. Assuming you've initialized your project with `winapp init`, this is all it takes to create an installable MSIX:

1. Build your application in `Release` configuration:
```
dotnet build -c Release
```
2. Generate a certificate with `winapp cert generate`, which will be named `devcert.pfx` by default:
```
winapp cert generate
```

3. Package your application with the `pack` command, specifying your output directory and the cert you just generated:
```
winapp pack .\bin\Release\net10.0-windows10.0.26100.0 --cert .\devcert.pfx
```

The `pack` command will output a signed MSIX, ready to install.

Before installing the MSIX, make sure to install the certificate:

```
winapp cert install .\devcert.pfx
```

And you're ready to test out your package!

<!-- image showing generated msix -->

For a more in-depth breakdown for these scenarios with .NET, checkout the full [.NET guide.](https://github.com/microsoft/winappCli/blob/main/docs/guides/dotnet.md)


## What else can winapp do?

Though package identity and packaging are the core of winapp CLI right now, it has other tools for Windows developers as well. Some highlights:

* Run Windows build tools directly with the `tool` command
* Command line based inspection and interaction with UI elements with the `ui` command
* Access to the Microsoft Store CLI via the `store` command 
* Support for other Windows-compatible frameworks for developers that work with a variety of stacks.

Check out the [Usage documentaition](https://github.com/microsoft/winappCli/blob/main/docs/usage.md) for a full breakdown of available commands.

## Don't like the terminal? Use the winapp VS Code extension instead.

If you prefer to stay inside Visual Studio Code, and want an integrated debugging experience, we recently launched a [VS Code Extension](https://marketplace.visualstudio.com/items?itemName=Microsoft-WinAppCLI.winapp) to expose winapp CLI functionality through VS Code commands.

With some brief configuration, you can enable a "F5" debug experience, allowing you to launch and test your app from within VS Code with a button press. The extension also exposes commands for packaging and cert generation via the command palette.

Once the extension is installed, hit `Ctrl+Shift+P` to open the command palette and type `winapp` to see available commands.

<!-- Image here of command palette open -->
 
 For more info on the winapp extension, check out the [release blog post.](https://devblogs.microsoft.com/ifdef-windows/announcing-the-winapp-vs-code-extension-run-debug-and-package-windows-apps-in-vs-code/)

## Get Started

Install the CLI and try it on your next project:

```
winget install Microsoft.winappcli --source winget
```

Or if you prefer to try out the extension, visit the [Visual Studio Marketplace.](https://marketplace.visualstudio.com/items?itemName=Microsoft-WinAppCLI.winapp)

If you find any issues, have a feature request, or want to check out the source for winapp, head over to our [GitHub Repository.](https://github.com/microsoft/winappCli)

For more getting started resources for .NET, check out the [.NET guide](https://github.com/microsoft/winappCli/blob/main/docs/guides/dotnet.md) or one of these samples:
- [.NET Console Application Sample](https://github.com/microsoft/winappCli/tree/main/samples/dotnet-app)
- [WPF Sample](https://github.com/microsoft/winappCli/tree/main/samples/wpf-app)

Happy packaging!