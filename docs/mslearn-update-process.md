# MS Learn Documentation Update Process

This document describes how winapp CLI documentation on Microsoft Learn is updated when a new release is published.

## Overview

On each release, the release pipeline automatically:
1. Runs `port-mslearn-docs.ps1` to transform repo docs into MS Learn format
2. Clones a fork of the MS Learn docs repo
3. Copies the ported docs and opens a PR
4. A human reviews and merges the PR

## Locations

| Location | Description |
|----------|-------------|
| **This repo**: `docs/` | Source of truth for documentation |
| **MS Learn repo**: `MicrosoftDocs/windows-dev-docs-pr` | Published docs on learn.microsoft.com |
| **MS Learn path**: `hub/apps/dev-tools/winapp-cli/` | Directory within the docs repo |
| **Live URL**: https://learn.microsoft.com/en-us/windows/apps/dev-tools/winapp-cli/ | Published site |

## What the port script does

`scripts/port-mslearn-docs.ps1` transforms docs for MS Learn:

1. **Copies mapped files** to an output directory matching the docs repo structure
2. **Adds YAML front matter** (`title`, `description`, `ms.date`, `ms.topic`)
3. **Rewrites links** — flattens electron paths, converts repo-internal links to GitHub URLs, preserves MS Learn cross-docs links
4. **Copies images** to `guides/media/`
5. **Generates `guides/index.md`** (framework table page)

### File mapping

| This repo | MS Learn docs repo |
|-----------|-------------------|
| `docs/index.md` | `index.md` |
| `docs/usage.md` | `usage.md` |
| `docs/guides/dotnet.md` | `guides/dotnet.md` |
| `docs/guides/cpp.md` | `guides/cpp.md` |
| `docs/guides/flutter.md` | `guides/flutter.md` |
| `docs/guides/rust.md` | `guides/rust.md` |
| `docs/guides/tauri.md` | `guides/tauri.md` |
| `docs/guides/packaging-cli.md` | `guides/packaging-cli.md` |
| `docs/guides/electron/setup.md` | `guides/electron-setup.md` |
| `docs/guides/electron/packaging.md` | `guides/electron-packaging.md` |
| `docs/guides/electron/phi-silica-addon.md` | `guides/electron-phi-silica-addon.md` |
| `docs/guides/electron/winml-addon.md` | `guides/electron-winml-addon.md` |
| `docs/guides/electron/cpp-notification-addon.md` | `guides/electron-cpp-notification-addon.md` |
| *(generated)* | `guides/index.md` |

## Running locally

```powershell
# Generate MS Learn-ready docs in artifacts/mslearn-docs/
.\scripts\port-mslearn-docs.ps1

# Custom output directory
.\scripts\port-mslearn-docs.ps1 -OutputPath "./my-output"
```

## Pipeline configuration

The `Release_MSLearn` stage in `.pipelines/release.yml` requires two pipeline variables:

| Variable | Description |
|----------|-------------|
| `MSLEARN_GH_TOKEN` | GitHub PAT with write access to the docs repo fork |
| `MSLearnDocsFork` | Fork of `MicrosoftDocs/windows-dev-docs-pr` (e.g., `myaccount/windows-dev-docs-pr`) |

## Adding new documentation pages

When adding a new doc page that should appear on MS Learn:

1. Add the file mapping to `$FileMapping` in `scripts/port-mslearn-docs.ps1`
2. Add front matter overrides to `$FrontMatterOverrides` (description, topic type)
3. If the page has links to repo-only content, add those paths to `$repoOnlyFiles`
4. If the page has images, add them to `$ImageMapping`
5. Update `docs/index.md` if the new page should appear in the commands overview or guides table
