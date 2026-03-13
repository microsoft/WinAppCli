# Using winapp CLI with .NET 

> This guide should work for most .NET projects types. The steps have been tested with both console and UI-based projects like WPF. For working examples, check out the [dotnet-app](../../samples/dotnet-app) (console) and [wpf-app](../../samples/wpf-app) (WPF) samples in the samples folder.

This guide demonstrates how to use `winappcli` with a .NET application to debug with package identity and package your application as an MSIX.

Package identity is a core concept in the Windows app model. It allows your application to access specific Windows APIs (like Notifications, Security, AI APIs, etc), have a clean install/uninstall experience, and more.

A standard executable (like one created with `dotnet build`) does not have package identity. This guide shows how to add it for debugging and then package it for distribution.

## Prerequisites

1.  **.NET SDK**: Install the .NET SDK:
    ```powershell
    winget install Microsoft.DotNet.SDK.10 --source winget
    ```

2.  **winapp CLI**: Install the `winapp` tool via winget:
    ```powershell
    winget install Microsoft.winappcli --source winget
    ```

## 1. Create a New .NET App

Start by creating a simple .NET console application:

```powershell
dotnet new console -n dotnet-app
cd dotnet-app
```

Run it to make sure everything is working:

```powershell
dotnet run
```
*Output should be "Hello, World!"*

## 2. Update Code to Check Identity

We'll update the app to check if it's running with package identity. We'll use the Windows Runtime API to access the Package APIs.

First, update your project file to target a specific Windows SDK version. Open `dotnet-app.csproj` and change the `TargetFramework` to include the Windows SDK version:

```xml
  <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
```

This gives you access to Windows Runtime APIs without needing additional packages.

Now replace the contents of `Program.cs` with the following code. This code attempts to retrieve the current package identity using the Windows Runtime API. If it succeeds, it prints the Package Family Name; otherwise, it prints "Not packaged".

```csharp
using Windows.ApplicationModel;

try
{
    var package = Package.Current;
    var familyName = package.Id.FamilyName;
    Console.WriteLine($"Package Family Name: {familyName}");
}
catch (InvalidOperationException)
{
    // Thrown when app doesn't have package identity
    Console.WriteLine("Not packaged");
}
```

## 3. Run Without Identity

Now, run the app as usual:

```powershell
dotnet run
```

You should see the output "Not packaged". This confirms that the standard executable is running without any package identity.

## 4. Initialize Project with winapp CLI

The `winapp init` command automatically detects `.csproj` files and runs a .NET-specific setup. It sets up everything you need in one go: validates your `TargetFramework`, adds required NuGet packages, generates the app manifest, and assets.

Run the following command and follow the prompts:

```powershell
winapp init
```

When prompted:
- **Package name**: Press Enter to accept the default (dotnet-app)
- **Publisher name**: Press Enter to accept the default or enter your name
- **Version**: Press Enter to accept 1.0.0.0
- **Description**: Press Enter to accept the default (Windows Application) or enter a description
- **Windows App SDK setup**: Select Stable, Preview, or Experimental (determines which Windows App SDK version is added)
- **TargetFramework update**: If your `TargetFramework` doesn't include a supported Windows SDK version, you'll be prompted to update it (e.g., to `net10.0-windows10.0.26100.0`)
- **Developer Mode**: If you are prompted about "Developer Mode", you can turn it on if you would like, but be aware that it requires administrative privileges

This command will:
- Update the `TargetFramework` in your `.csproj` to a supported Windows TFM (if needed)
- Add `Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools`, and `Microsoft.Windows.SDK.BuildTools.WinApp` NuGet package references to your `.csproj`
- Create `appxmanifest.xml` and `Assets` folder for your app identity

> **Note:** Unlike native/C++ projects, the .NET flow does **not** create a `winapp.yaml` file. NuGet packages are managed directly via your `.csproj`. Use `dotnet restore` to restore packages after cloning.

You can open `appxmanifest.xml` to further customize properties like the display name, publisher, and capabilities.

## 5. Debug with Identity

Since `winapp init` added the `Microsoft.Windows.SDK.BuildTools.WinApp` NuGet package to your project, you can simply run:

```powershell
dotnet run
```

This automatically invokes `winapp run` under the hood — creating a loose layout package, registering it with Windows, and launching your app with full package identity.

> **Console apps:** By default, AUMID activation opens a new window. For console applications that need stdin/stdout in the current terminal, add `<WinAppRunUseExecutionAlias>true</WinAppRunUseExecutionAlias>` to your `.csproj` and ensure your manifest has a `uap5:ExecutionAlias`. You can add one with `winapp manifest add-alias`.

You should see output similar to:
```
Package Family Name: dotnet-app_12345abcde
```
This confirms your app is running with a valid package identity!

### Alternative: Manual `winapp run`

If you didn't use `winapp init` (or removed the NuGet package), you can build and run manually:

```powershell
dotnet build -c Debug
winapp run .\bin\Debug\net10.0-windows10.0.26100.0
```

To add the NuGet package back: `dotnet add package Microsoft.Windows.SDK.BuildTools.WinApp --prerelease`

> **Tip:** To disable the automatic `dotnet run` integration, add `<EnableWinAppRunSupport>false</EnableWinAppRunSupport>` to your `.csproj`. See [dotnet run support docs](../dotnet-run-support.md) for customization options.

### Alternative: Sparse package identity

If you need sparse package behavior specifically (identity without copying files), you can use `create-debug-identity` instead. This registers a sparse package pointing to your exe rather than creating a loose layout:

```powershell
winapp create-debug-identity .\bin\Debug\net10.0-windows10.0.26100.0\dotnet-app.exe
```

Then run the executable directly (do not use `dotnet run` as it might rebuild/overwrite the file):
```powershell
.\bin\Debug\net10.0-windows10.0.26100.0\dotnet-app.exe
```

### Alternative: Manual MSBuild target

If you prefer not to use the NuGet package, you can add a custom MSBuild target that runs `create-debug-identity` after Debug builds. Add this to your `.csproj` file at the end, just before the closing `</Project>` tag:

```xml
  <!-- Automatically apply debug identity after Debug builds -->
  <Target Name="ApplyDebugIdentity" AfterTargets="Build" Condition="'$(Configuration)' == 'Debug'">
    <Exec Command="winapp create-debug-identity &quot;$(TargetDir)$(TargetName).exe&quot;" 
          WorkingDirectory="$(ProjectDir)" 
          IgnoreExitCode="false" />
  </Target>
```

With this configuration, `dotnet build` applies the debug identity and you can run the executable directly. Note that `dotnet run` may rebuild and overwrite the identity, so run the exe manually after building.

> **Tip:** For advanced debugging workflows (attaching debuggers, IDE setup, startup debugging), see the [Debugging Guide](../debugging.md).

## 6. Using Windows App SDK

If you ran `winapp init` (Step 4), `Microsoft.WindowsAppSDK` was already added as a NuGet package reference to your `.csproj`. If you skipped SDK setup during init, or need to add it manually, run:

```powershell
dotnet add package Microsoft.WindowsAppSDK
```

### Update Program.cs

Let's update the app to use the Windows App Runtime API to get the runtime version:

```csharp
using Windows.ApplicationModel;

class Program
{
    static void Main(string[] args)
    {
        try
        {
            var package = Package.Current;
            var familyName = package.Id.FamilyName;
            Console.WriteLine($"Package Family Name: {familyName}");
            
            // Get Windows App Runtime version using the API
            var runtimeVersion = Microsoft.Windows.ApplicationModel.WindowsAppRuntime.RuntimeInfo.AsString;
            Console.WriteLine($"Windows App Runtime Version: {runtimeVersion}");
        }
        catch (InvalidOperationException)
        {
            // Thrown when app doesn't have package identity
            Console.WriteLine("Not packaged");
        }
    }
}
```

### Build and Run

Rebuild and run the application with Windows App SDK. Since we've added the WinAppSDK, we need to re-register with identity so `winapp` adds the runtime dependency. If you added the WinApp NuGet package (recommended), simply run `dotnet run`. Otherwise:

```powershell
dotnet build -c Debug
winapp run .\bin\Debug\net10.0-windows10.0.26100.0
```

You should now see output like:
```
Package Family Name: dotnet-app.debug_12345abcde
Windows App Runtime Version: 8000.770.947.0
```

The Windows App SDK NuGet package includes all the necessary assemblies for accessing modern Windows APIs including:
- Notifications and live tiles
- Windowing and app lifecycle
- Push notifications
- And many more Windows App SDK components

For more advanced Windows App SDK usage, check out the [Windows App SDK documentation](https://learn.microsoft.com/windows/apps/windows-app-sdk/).

## 7. Package with MSIX

Once you're ready to distribute your app, you can package it as an MSIX using the same manifest.

### Build for Release
First, build your application in release mode for optimal performance:

```powershell
dotnet build -c Release
```

### Add Execution Alias (for console apps)
To allow users to run your app from the command line after installation (like `dotnet-app`), add an execution alias to the `appxmanifest.xml`. If you are building a WPF or WinForms app, this step is not necessary. 

Open `appxmanifest.xml` and add the `uap5` namespace to the `<Package>` tag if it's missing, and then add the extension inside `<Applications><Application><Extensions>...`:

```xml
<Package
  ...
  xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
  xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
  IgnorableNamespaces="uap uap2 uap3 rescap desktop desktop6 uap10">

  ...
  <Applications>
    <Application ...>
      ...

      <!-- Add this Extensions element in your manifest 
           along with the xmlns:uap5 namespace above -->
      <Extensions>
        <uap5:Extension Category="windows.appExecutionAlias">
          <uap5:AppExecutionAlias>
            <uap5:ExecutionAlias Alias="dotnet-app.exe" />
          </uap5:AppExecutionAlias>
        </uap5:Extension>
      </Extensions>

      ...
    </Application>
  </Applications>
</Package>
```

### Generate a Development Certificate

Before packaging, you need a development certificate for signing. Generate one if you haven't already:

```powershell
winapp cert generate --if-exists skip
```

### Sign and Pack

Now you can package and sign. Point the pack command to your build output folder:

```powershell
# package and sign the app with the generated certificate
winapp pack .\bin\Release\net10.0-windows10.0.26100.0 --manifest .\appxmanifest.xml --cert .\devcert.pfx 
```

> Note: The `pack` command automatically uses the appxmanifest.xml from your current directory and copies it to the target folder before packaging. The generated .msix file will be in the current directory.

### Install the Certificate

Before you can install the MSIX package, you need to install the development certificate. Run this command as administrator:

```powershell
winapp cert install .\devcert.pfx
```

### Install and Run
Install the package by double-clicking the generated *.msix file.

Now you can run your app from anywhere in the terminal by typing:

```powershell
dotnet-app
```

You should see the "Package Family Name" output, confirming it's installed and running with identity.

### Tips:
1. Once you are ready for distribution, you can sign your MSIX with a code signing certificate from a Certificate Authority so your users don't have to install a self-signed certificate.
2. The Microsoft Store will sign the MSIX for you, no need to sign before submission.
3. You might need to create multiple MSIX packages, one for each architecture you support (x64, Arm64). Use the `-r` flag with `dotnet build` to target specific architectures: `dotnet build -c Release -r win-x64` or `dotnet build -c Release -r win-arm64`.

### Automating MSIX Packaging (Optional)

To automate MSIX packaging as part of your Release builds, add this target to your `.csproj` file (you can add it alongside the debug identity target):

```xml
  <!-- Automatically package as MSIX after Release builds -->
  <Target Name="PackageMsix" AfterTargets="Build" Condition="'$(Configuration)' == 'Release'">
    <!-- Package and sign directly from build output -->
    <Exec Command="winapp pack &quot;$(TargetDir.TrimEnd('\'))&quot; --cert &quot;$(ProjectDir)devcert.pfx&quot;" 
          WorkingDirectory="$(ProjectDir)" 
          IgnoreExitCode="false" />
  </Target>
```

With this configuration:
- Building in Release mode (`dotnet build -c Release`) will automatically create the MSIX package
- The MSIX is packaged and signed with your development certificate
- The final `.msix` file will be in the root of the project

You can also create a custom configuration (e.g., `PackagedRelease`) by modifying the condition to `'$(Configuration)' == 'PackagedRelease'`.
