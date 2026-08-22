---
name: winapp-sandbox
description: Run, debug, and UI-automate a Windows app inside a persistent Windows Sandbox instead of the user's own desktop, using winapp's --sandbox option. Use when an agent needs to launch or automate an app without stealing the user's focus, cursor, or keyboard, when UI automation must not disturb the machine it runs on, or when an app should be exercised in a disposable Windows environment. Also covers running arbitrary commands and copying files into that Sandbox.
---
## When to use

- An agent needs to click, type into, screenshot, or record an app **without** taking over the user's desktop
- UI automation must keep running while the user keeps working
- An app should be exercised in a disposable Windows environment and thrown away afterwards
- A dependency has to be installed, or a diagnostic run, inside that environment

Builds still happen on the host and stay fast. Only running, debugging, and automating move.

## Prerequisites

- Windows 11 24H2 or newer, on a supported edition, with hardware virtualization enabled
- The Windows Sandbox optional feature installed, and a working `wsb.exe`
- An unlocked interactive host session while a command needs real input or screen recording

winapp does not enable Windows features or reboot. Missing prerequisites fail **before** the app is
built, and there is **no silent fallback to running locally** — a command that asked for Sandbox
either runs there or fails.

## Common patterns

### Run an app and automate it

```powershell
winapp run . --sandbox
winapp ui inspect --sandbox -a MyApp
winapp ui invoke --sandbox SubmitButton -a MyApp
winapp ui screenshot --sandbox -a MyApp -o .\result.png
```

The Sandbox stays running between commands and between rebuilds, so the second run transfers only
what changed.

### Capture evidence

```powershell
winapp ui screenshot --sandbox -a MyApp -o .\before.png
winapp ui record --sandbox -a MyApp --duration-sec 5 -o .\demo.mp4
```

`-o` lands at the host path given. The file is verified against the size and hash the guest reported
before it is published, so an interrupted transfer never leaves a plausible-looking partial result.

### Iterate

```powershell
winapp run . --sandbox --detach   # returns once the app is up
winapp ui list-windows --sandbox  # discover targets
winapp run . --sandbox --clean    # fresh application data
winapp unregister --sandbox       # remove just this app from the Sandbox
```

### Prepare dependencies or diagnose

```powershell
winapp sandbox exec -- dotnet --info
winapp sandbox cp .\setup.ps1 sandbox:C:\Setup\setup.ps1
winapp sandbox exec --cwd C:\Setup -- powershell -File .\setup.ps1
winapp sandbox cp sandbox:C:\Results .\results
```

`sandbox cp` requires exactly one endpoint prefixed with `sandbox:`, so the direction is never
guessed.

## What to know before relying on it

**Targets must be explicit.** `--app` or `--window` is required, exactly as locally. Use
`winapp ui list-windows --sandbox` to discover them; no command infers the most recent launch.

**Guest process IDs and window handles are scoped.** They are valid only inside the Sandbox
generation that produced them. After the Sandbox is recreated, stale values are rejected rather than
resolved against the new one.

**A `sandbox:` prefix is an alternative to the flag** for string app targets — `-a sandbox:MyApp`.
A numeric `--window` needs `--sandbox`, because a handle carries no scope of its own.

**Every run option keeps its meaning** — `--detach`, `--debug-output`, `--no-launch`, `--clean`,
`--unregister-on-exit`, `--with-alias`, `--json` — because the Sandbox runs the ordinary
`winapp run`. The exception: `--debug-output` is not available for an *unpackaged* app in Sandbox and
is refused up front.

**`--sandbox` isolates the running app, not the build.** Project evaluation, restore, and compilation
happen on the host, so it does not make an untrusted project safe to open. Everything inside the
Sandbox is one trust boundary: apps sharing it share the user account, desktop, registry, runtimes,
and network, and can interfere with one another.

**Shared runtimes are provisioned from your caches.** Before deploying, winapp reads the app's
package-manifest dependencies, unpackaged Windows App SDK version from `*.deps.json`, and
`*.runtimeconfig.json`, then stages and installs what the Sandbox is missing — never on your machine,
and never over a version already registered. A Windows App
Runtime dependency brings its whole cached inventory (Framework, DDLM, Main, Singleton), and shared
.NET runtimes are unpacked from official runtime packs into a per-user .NET root inside the guest,
with the app launched against it. The complete graph is verified before every launch, because
`sandbox exec` can change guest runtime state between runs. Anything that cannot be satisfied fails
with `sandbox_runtime_provision_failed` naming it, before launch. Publishing self-contained avoids
the requirement entirely.

**winapp never touches a Sandbox it did not create.** If one is already running, the command reports
its ID and stops; stopping it is the user's decision. winapp also never shuts a Sandbox down on its
own — use `wsb list`, `wsb connect --id <id>`, `wsb stop --id <id>`.

**The Sandbox window must stay connected** for real input and screen recording. Closing it leaves
inspection working while input and recording stop; winapp reports `sandbox_input_not_ready` rather
than claiming input it did not deliver. `wsb connect` restores the same session and both
capabilities.

## Troubleshooting

| Error code | What it means | What to do |
|---|---|---|
| `sandbox_unsupported` | This machine cannot run Windows Sandbox | Check the edition, virtualization, and that the optional feature is installed |
| `sandbox_unmanaged_instance` | A Sandbox winapp did not create is running | Close it if it is safe, then retry. winapp will not stop it for you |
| `sandbox_no_interactive_session` | No interactive guest session | Reconnect the Sandbox window with `wsb connect` |
| `sandbox_input_not_ready` | Input could not be delivered — and none was reported as delivered | Reconnect and un-minimize the Sandbox window |
| `sandbox_terminated` | The Sandbox went away mid-command | Retry; the next command recreates and redeploys |
| `sandbox_deployment_dirty` | The guest copy is incomplete, so it will not launch | Run the command again to redeploy completely |
| `sandbox_transfer_interrupted` | A transfer stopped; nothing was published | Retry. The error names the artifact, expected size, and what arrived |
| `sandbox_agent_incompatible` | The guest agent needs a newer winapp | `winapp update` |
| `sandbox_runtime_provision_failed` | A runtime the app requires is missing from the Sandbox | The error names it. Publish self-contained, or install it with `winapp sandbox exec` |

Infrastructure failures use codes distinct from your app's exit codes, so "winapp could not run your
app" is always distinguishable from "your app failed".

## Full documentation

See [Windows Sandbox execution](https://github.com/microsoft/WinAppCli/blob/main/docs/sandbox-execution.md)
for the lifecycle, deployment, coordination, and architecture details.
