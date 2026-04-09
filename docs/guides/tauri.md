# Using winapp CLI with Tauri

This guide demonstrates how to use `winappcli` with a Tauri application to debug with package identity and package your application as an MSIX.

Package identity is a core concept in the Windows app model. It allows your application to access specific Windows APIs (like Notifications, Security, AI APIs, etc), have a clean install/uninstall experience, and more.

For a complete working example, check out the [Tauri sample](../../samples/tauri-app) in this repository.

## Prerequisites

1. **Windows 11** 
1. **Node.js** - `winget install OpenJS.NodeJS --source winget`
1. **winapp CLI** - `winget install microsoft.winappcli --source winget`

## 1. Create a New Tauri App

Start by creating a new Tauri application using the official scaffolding tool:

```powershell
npm create tauri-app@latest
```
Follow the prompts (e.g., Project name: `tauri-app`, Frontend language: `JavaScript`, Package manager: `npm`).

Navigate to your project directory and install dependencies:

```powershell
cd tauri-app
npm install
```

Run the app to make sure everything is working:

```powershell
npm run tauri dev
```

## 2. Update Code to Check Identity

We'll update the app to check if it's running with package identity. We'll use the `windows` crate in the Rust backend to access Windows APIs and expose it to the frontend.

### Backend Changes (Rust)

1.  **Add Dependency**: Open `src-tauri/Cargo.toml` and add the `windows` dependency for the Windows target:

    ```toml
    [target.'cfg(windows)'.dependencies]
    windows = { version = "0.58", features = ["ApplicationModel"] }
    ```

2.  **Add Command**: Open `src-tauri/src/lib.rs` and add the `get_package_family_name` command. This function attempts to retrieve the current package identity.

    ```rust
    #[tauri::command]
    fn get_package_family_name() -> String {
        #[cfg(target_os = "windows")]
        {
            use windows::ApplicationModel::Package;
            match Package::Current() {
                Ok(package) => {
                    match package.Id() {
                        Ok(id) => match id.FamilyName() {
                            Ok(name) => name.to_string(),
                            Err(_) => "Error retrieving Family Name".to_string(),
                        },
                        Err(_) => "Error retrieving Package ID".to_string(),
                    }
                }
                Err(_) => "No package identity".to_string(),
            }
        }
        #[cfg(not(target_os = "windows"))]
        {
            "Not running on Windows".to_string()
        }
    }
    ```

3.  **Register Command**: In the same file (`src-tauri/src/lib.rs`), update the `run` function to register the new command:

    ```rust
    pub fn run() {
        tauri::Builder::default()
            .plugin(tauri_plugin_opener::init())
            .invoke_handler(tauri::generate_handler![greet, get_package_family_name]) // Add get_package_family_name here
            .run(tauri::generate_context!())
            .expect("error while running tauri application");
    }
    ```

### Frontend Changes (JavaScript)

1.  **Update HTML**: Open `src/index.html` and add a paragraph to display the result:

    ```html
    <!-- ... inside <main> ... -->
    <p id="pfn-msg"></p>
    ```

2.  **Update Logic**: Open `src/main.js` to invoke the command and display the result:

    ```javascript
    const { invoke } = window.__TAURI__.core;

    // ... existing code ...

    async function checkPackageIdentity() {
      const pfn = await invoke("get_package_family_name");
      const pfnMsgEl = document.querySelector("#pfn-msg");
      
      if (pfn !== "No package identity" && !pfn.startsWith("Error")) {
        pfnMsgEl.textContent = `Package family name: ${pfn}`;
      } else {
        pfnMsgEl.textContent = `Not running with package identity`;
      }
    }

    window.addEventListener("DOMContentLoaded", () => {
      // ... existing code ...
      checkPackageIdentity();
    });
    ```

3. Now, run the app as usual:

    ```powershell
    npm run tauri dev
    ```

    You should see "Not running with package identity" in the app window. This confirms that the standard development build is running without package identity.

## 3. Initialize Project with winapp CLI

The `winapp init` command sets up everything you need in one go: app manifest and assets.

Run the following command and follow the prompts:

```powershell
winapp init
```

When prompted:
- **Package name**: Press Enter to accept the default (tauri-app)
- **Publisher name**: Press Enter to accept the default or enter your name
- **Version**: Press Enter to accept 1.0.0.0
- **Entry point**: Press Enter to accept the default (tauri-app.exe)
- **Setup SDKs**: Select "Do not setup SDKs"

This command will:
- Create `appxmanifest.xml` and `Assets` folder for your app identity

You can open `appxmanifest.xml` to further customize properties like the display name, publisher, and capabilities.

## 4. Debug with Identity

To debug with identity, we need to build the Rust backend and run it with `winapp run`. Since `npm run tauri dev` manages the process lifecycle, it's harder to inject the identity there. Instead, we'll create a custom script.

1.  **Add Script**: Open `package.json` and add a new script `tauri:dev:withidentity`:

    ```json
    "scripts": {
      "tauri": "tauri",
      "tauri:dev:withidentity": "cargo build --manifest-path src-tauri/Cargo.toml && (if not exist dist mkdir dist) && copy /Y src-tauri\\target\\debug\\tauri-app.exe dist\\ >nul && winapp run .\\dist"
    }
    ```

    **What this script does:**
    *   `cargo build ...`: Recompiles the Rust backend.
    *   `copy ... dist\\`: Stages just the exe into a `dist` folder (the `target\debug` folder is very large and contains intermediate build artifacts that aren't part of your app).
    *   `winapp run .\\dist`: Registers a loose layout package (just like a real MSIX install) and launches the app.

2.  **Run the Script**:

    ```powershell
    npm run tauri:dev:withidentity
    ```

You should now see the app open and display a "Package family name", confirming it is running with identity! You can now start using and debugging APIs that require package identity, such as Notifications or the new AI APIs like Phi Silica. 

> **Tip:** For advanced debugging workflows (attaching debuggers, IDE setup, startup debugging), see the [Debugging Guide](../debugging.md).

## 5. Package with MSIX

Once you're ready to distribute your app, you can package it as an MSIX which will provide the package identity to your application.

First, add a `pack:msix` script to your `package.json`:

```json
"scripts": {
  "tauri": "tauri",
  "tauri:dev:withidentity": "...",
  "pack:msix": "npm run tauri -- build && (if not exist dist mkdir dist) && copy /Y src-tauri\\target\\release\\tauri-app.exe dist\\ >nul && winapp pack .\\dist --cert .\\devcert.pfx"
}
```

**What this script does:**
*   `npm run tauri -- build`: Builds the Rust backend in release mode.
*   `copy ... dist\\`: Stages just the exe into a `dist` folder (the `target\release` folder is very large and contains intermediate build artifacts that aren't part of your app).
*   `winapp pack .\\dist --cert .\\devcert.pfx`: Packages and signs the app as MSIX.

### Generate a Development Certificate

Before packaging, you need a development certificate for signing. Generate one if you haven't already:

```powershell
winapp cert generate --if-exists skip
```

### Build, Stage, and Pack

```powershell
npm run pack:msix
```

> Note: The `pack` command automatically uses the appxmanifest.xml from your current directory and copies it to the target folder before packaging. The generated .msix file will be in the current directory.

### Install the Certificate

Before you can install the MSIX package, you need to install the development certificate. Run this command as administrator:

```powershell
winapp cert install .\devcert.pfx
```

### Install and Run
Install the package by double-clicking the generated `.msix` file. Once installed, you can launch your app from the start menu.


You should see the app running with identity.
