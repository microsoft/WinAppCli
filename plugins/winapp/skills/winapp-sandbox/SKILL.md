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
winapp run . --sandbox --detach   # returns once the app is up (see the note below)
winapp ui list-windows --sandbox  # discover targets
winapp run . --sandbox --clean    # fresh application data
winapp unregister --sandbox       # remove just this app from the Sandbox
```

Several winapp commands can use one Sandbox at the same time, so a foreground `winapp run .
--sandbox` running in one terminal does not hold up `winapp ui list-windows --sandbox` in another.
Past eight commands at once the next one fails immediately with `sandbox_agent_busy` instead of
waiting. Deployment and registration still take turns, so two commands never redeploy at once.

With `--detach` on an **unpackaged** app, expect the app to last only as long as the current guest
agent. It is started as a child of the agent, and the agent deliberately contains everything it starts
so a cancelled command cannot strand guest processes holding files the next deployment must replace.
The same containment ends the app when the agent does — including when winapp repairs the agent
automatically, which it does without asking if a later command finds it unresponsive or replaces it
after a winapp upgrade. Nothing reports an error at that moment, because nothing was waiting on the
app. Rerun `winapp run . --sandbox --detach` to bring it back, and prefer a foreground run when the app
must survive a long automation sequence. A packaged app is activated by Windows rather than started by
the agent and was observed to survive an agent repair that ended an unpackaged one; closing or
restarting the Sandbox still ends both.

### Prepare dependencies or diagnose

```powershell
winapp sandbox exec -- dotnet --info
winapp sandbox cp .\setup.ps1 sandbox:Setup\setup.ps1
winapp sandbox exec --cwd C:\WinApp\work\Setup -- powershell -ExecutionPolicy Bypass -File .\setup.ps1
winapp sandbox cp sandbox:Results .\results
```

`-ExecutionPolicy Bypass` belongs in that command. A fresh Sandbox starts at `Restricted`, so a script
you just copied in is refused with `UnauthorizedAccess` without it.

`sandbox cp` requires exactly one endpoint prefixed with `sandbox:`, so the direction is never
guessed. Symbolic links and junctions in a host source are not followed — only what is genuinely
inside the folder you named is copied.

**Guest paths are relative to `C:\WinApp\work`.** That is the folder winapp manages inside the
Sandbox, and resolving every guest path against it is what keeps a path you pass from selecting an
arbitrary location. A drive-absolute, rooted, or UNC guest path is refused with a message naming the
work root rather than quietly re-rooted, so a copy never reports success for a file that is not where
you asked for it. Each copy into the Sandbox prints the resolved destination — use that as the
`--cwd` of whatever you run next.

A single file lands at exactly the destination you name: `sandbox:Setup\setup.ps1` *is* the file, not
a folder to place it in. A directory keeps its own structure beneath the destination.

## What to know before relying on it

**The Sandbox stays and is reused.** The instance, its agent, and your deployment persist between
commands, so `winapp run . --sandbox` followed by several `winapp ui ... --sandbox` commands is one
environment, not several. A later command reconnects to the agent that is already running rather than
restarting it, and read-only verbs (`inspect`, `search`, `get-property`, `get-focused`,
`list-windows`, `wait-for`, `status`) do **not** reconnect the Sandbox window — so they never
interrupt what is on screen. If the agent has stopped, the next command repairs it inside the same
Sandbox; your deployment survives.

**Slow phases report progress on stderr.** Starting or reusing the Sandbox, preparing the agent,
checking runtimes, deploying, and launching each print a line before they begin. Under `--json`,
stdout still carries exactly one machine-readable document — progress never goes there.

**No firewall prompt.** The host assigns the agent's port and creates a narrow inbound rule for that
program and port before the agent starts listening, so Windows never raises its consent dialog inside
the Sandbox.

**Upgrading winapp needs a fresh Sandbox.** A running agent holds the staged binary, so a newer
winapp cannot replace it in place. It says so and asks you to close the Sandbox rather than failing
with a file error.

**Targets must be explicit.** `--app` or `--window` is required, exactly as locally. Use
`winapp ui list-windows --sandbox` to discover them; no command infers the most recent launch.

**Guest process IDs and window handles are scoped.** They are valid only inside the Sandbox
generation that produced them. After the Sandbox is recreated, stale values are rejected rather than
resolved against the new one.

**A `sandbox:` prefix is an alternative to the flag** for string app targets — `-a sandbox:MyApp`.
A numeric `--window` needs `--sandbox`, because a handle carries no scope of its own.

**Every run option keeps its meaning** — `--detach`, `--debug-output`, `--no-launch`, `--clean`,
`--unregister-on-exit`, `--with-alias`, `--json` — because the Sandbox runs the ordinary
`winapp run`. Two limits: `--debug-output` is not available for an *unpackaged* app in Sandbox and is
refused up front, and `--detach` on an unpackaged app gives you a process that lasts only as long as
the current guest agent, as described under *Iterate* above.

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
with the app launched against it. The one package winapp downloads — the desktop VC runtime — must
pass an Authenticode Microsoft-signature check *and* an identity/version/architecture/publisher check
before it is cached, so a rejected payload never poisons the host cache. The complete graph is
verified before every launch, because
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
| `sandbox_agent_incompatible` | The guest agent needs a newer winapp, or a different winapp version is already running it | `winapp update`. If you upgraded winapp while a Sandbox was running, close the Sandbox so a fresh agent starts |
| `sandbox_agent_busy` | Eight winapp commands are already using this Sandbox | Wait for one to finish, then retry |
| `sandbox_runtime_provision_failed` | A runtime the app requires is missing from the Sandbox | The error names it. Publish self-contained, or install it with `winapp sandbox exec` |

Infrastructure failures use codes distinct from your app's exit codes, so "winapp could not run your
app" is always distinguishable from "your app failed".

Two problems report no winapp error code at all:

| Symptom | Cause | What to do |
|---|---|---|
| A detached **unpackaged** app is gone, and no command reported it stopping | It was started by the guest agent and ended with it, most often because winapp automatically repaired the agent | Rerun `winapp run . --sandbox --detach`. For an app that must survive a long automation sequence, run it in the foreground instead |
| A script copied in with `sandbox cp` fails with `UnauthorizedAccess` | A fresh Sandbox starts with the PowerShell execution policy at `Restricted` | Run it as `powershell -ExecutionPolicy Bypass -File .\script.ps1` |

## Full documentation

See [Windows Sandbox execution](https://github.com/microsoft/WinAppCli/blob/main/docs/sandbox-execution.md)
for the lifecycle, deployment, coordination, and architecture details.
