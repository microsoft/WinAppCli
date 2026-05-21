## Node.js CLI commands

These commands are available exclusively via `npx winapp node <subcommand>` and are not exported as programmatic functions.

### `node create-addon`

Generate native addon files for an Electron project.  Supports C++ (node-gyp) and C# (node-api-dotnet) templates.

```bash
npx winapp node create-addon [options]
```

**Options:**

| Flag | Description |
|------|-------------|
| `--name <name>` | Addon name (default depends on template) |
| `--template <type>` | Addon template: `cpp` or `cs` (default: `cpp`) |
| `--verbose` | Enable verbose output |

> **Note:** Must be run from the root of an Electron project (directory containing `package.json`).

**Examples:**

```bash
npx winapp node create-addon
npx winapp node create-addon --name myAddon
npx winapp node create-addon --template cs --name MyCsAddon
```

---

### `node add-electron-debug-identity`

Add package identity to the Electron debug process using sparse packaging.  Creates a backup of `electron.exe`, generates a sparse MSIX manifest, adds identity to the executable, and registers the sparse package.  Requires a `Package.appxmanifest` (create one with `winapp init` or `winapp manifest generate`).

```bash
npx winapp node add-electron-debug-identity [options]
```

**Options:**

| Flag | Description |
|------|-------------|
| `--manifest <path>` | Path to custom `Package.appxmanifest` (default: `Package.appxmanifest` in current directory) |
| `--no-install` | Do not install the package after creation |
| `--keep-identity` | Keep the manifest identity as-is, without appending `.debug` suffix |
| `--verbose` | Enable verbose output |

> **Note:** Must be run from the root of an Electron project (directory containing `node_modules/electron`).  To undo, use `npx winapp node clear-electron-debug-identity`.

**Examples:**

```bash
npx winapp node add-electron-debug-identity
npx winapp node add-electron-debug-identity --manifest ./custom/Package.appxmanifest
```

---

### `node clear-electron-debug-identity`

Remove package identity from the Electron debug process.  Restores `electron.exe` from the backup created by `add-electron-debug-identity` and removes the backup files.

```bash
npx winapp node clear-electron-debug-identity [options]
```

**Options:**

| Flag | Description |
|------|-------------|
| `--verbose` | Enable verbose output |

> **Note:** Must be run from the root of an Electron project (directory containing `node_modules/electron`).

**Examples:**

```bash
npx winapp node clear-electron-debug-identity
```

---
