# Packaging a CLI Executable as MSIX

This guide walks you through packaging an existing command-line executable as an MSIX package for distribution via Windows Package Manager (winget), the Microsoft Store, or direct distribution.

## Prerequisites

- An existing CLI executable (`.exe`) that you want to package
- Windows 10 version 1809 or later


## Steps

### 1. Organize Your CLI Application

Place your CLI executable and any dependencies in a dedicated folder. This folder will contain all files that should be included in your MSIX package.

```powershell
mkdir MyCliPackage
cd MyCliPackage
# Copy your CLI executable and dependencies here
```

### 2. Install winapp CLI

Install the winapp CLI via Windows Package Manager, or update to the latest version if you already have it:

```powershell
# Install (or update if already installed)
winget install microsoft.winappcli --source winget
```

### 3. Generate the Package.appxmanifest

Generate a base Package.appxmanifest and required assets for your CLI executable:

```powershell
winapp manifest generate --executable .\yourcli.exe
```

This command creates a `Package.appxmanifest` file in the current directory with default values populated from your executable.

### 4. Configure the Manifest

Edit the generated `Package.appxmanifest` to customize your package. Each sub-step below explains what to change and why.

#### 4.1 Add Required Namespace

Add the `uap5` namespace to the `Package` element if it's not already present. This is needed for the execution alias in step 4.3:

```xml
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  ...
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap uap5 rescap">
```

#### 4.2 Configure the Application Element

In the `<uap:VisualElements>` element, add `AppListEntry="none"` to hide the app from the Start menu. CLI tools are invoked from the terminal, so they don't need a Start menu entry:

```xml
<uap:VisualElements
    DisplayName="YourApp"
    Description="My Application"
    BackgroundColor="transparent"
    Square150x150Logo="Assets\Square150x150Logo.png"
    Square44x44Logo="Assets\Square44x44Logo.png"
    AppListEntry="none">
</uap:VisualElements>
```

#### 4.3 Add Execution Alias Extension

Add an execution alias so users can run your CLI by name from any terminal window. Add this within the `<Application>` element (after `<uap:VisualElements>`):

```xml
<Extensions>
  <uap5:Extension Category="windows.appExecutionAlias">
    <uap5:AppExecutionAlias>
      <uap5:ExecutionAlias Alias="yourcli.exe" />
    </uap5:AppExecutionAlias>
  </uap5:Extension>
</Extensions>
```

Replace `yourcli.exe` with the desired command name for your CLI. Once a user installs the MSIX, they will be able to invoke your CLI with this command.

#### 4.4 Update Application Metadata

Update the following fields to match your CLI application.

> **Important**: The `Publisher` value in your manifest must match the publisher in your signing certificate. If you generate a certificate later (step 5), it will use the publisher from your manifest. If you change the publisher after generating a certificate, you'll need to regenerate the certificate to match.

- **Identity**: Update `Name`, `Publisher`, and `Version`
  ```xml
  <Identity
    Name="YourCompany.YourCLI"
    Publisher="CN=Your Company"
    Version="1.0.0.0" />
  ```

- **Properties**: Update display name, publisher display name, and description
  ```xml
  <Properties>
    <DisplayName>Your CLI Tool</DisplayName>
    <PublisherDisplayName>Your Company</PublisherDisplayName>
    <Description>Description of your CLI tool</Description>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  ```

- **VisualElements**: Update display name and asset references
  ```xml
  <uap:VisualElements
    DisplayName="Your CLI Tool"
    Description="Description of your CLI tool"
    BackgroundColor="transparent"
    Square150x150Logo="Assets\Square150x150Logo.png"
    Square44x44Logo="Assets\Square44x44Logo.png">
    <uap:DefaultTile Wide310x150Logo="Assets\Wide310x150Logo.png" />
    <uap:SplashScreen Image="Assets\SplashScreen.png" />
  </uap:VisualElements>
  ```

**Note**: You should also add proper icon assets to an `Assets` folder in your package directory. While the app won't appear in the Start menu, icons are still required for Store submission and may appear in other contexts.

### 5. (Optional) Generate a Development Certificate

For local testing and distribution outside the Microsoft Store, you'll need to sign your MSIX package with a certificate.

Generate a development certificate. Keep it outside your CLI folder to avoid accidentally including it in the package:

```powershell
# Navigate to a location outside your CLI folder (e.g., your home directory)
cd ~
winapp cert generate
```

This creates a `devcert.pfx` file in your home directory (e.g., `C:\Users\yourname\devcert.pfx`).

To trust this certificate on your development machine, install it (requires administrator privileges):

```powershell
# Run PowerShell as Administrator
winapp cert install ~\devcert.pfx
```

### 6. Package Your CLI

Now you're ready to create the MSIX package:

```powershell
# Navigate back outside of your project folder
# Package with dev certificate (for local testing/distribution)
winapp pack .\path\to\MyCliPackage --cert .\path\to\devcert.pfx
```

This creates an `.msix` file in the current directory.

### 7. Install and Verify

Install the MSIX package to verify everything works:

```powershell
Add-AppxPackage .\MyCliPackage.msix
```

If you added an execution alias in step 4.3, you can now run your CLI from any terminal:

```powershell
yourcli --help
```

To uninstall later:

```powershell
Get-AppxPackage *YourCLI* | Remove-AppxPackage
```

## Tips

1. Once you are ready for distribution, you can sign your MSIX with a code signing certificate from a Certificate Authority so your users don't have to install a self-signed certificate
2. The Microsoft Store will sign the MSIX for you, no need to sign before submission.
3. You might need to create multiple MSIX packages, one for each architecture you support (x64, Arm64)

## Next Steps

- **Distribute via winget**: Submit your MSIX to the [Windows Package Manager Community Repository](https://github.com/microsoft/winget-pkgs)
- **Publish to the Microsoft Store**: Use `winapp store` to submit your package
- **Set up CI/CD**: Use the [`setup-WinAppCli`](https://github.com/microsoft/setup-WinAppCli) GitHub Action to automate packaging in your pipeline
