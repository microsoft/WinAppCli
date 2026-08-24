<!-- mslearn: true -->
<!-- ms.topic: concept-article -->
<!-- description: Run, debug, and UI-automate Windows applications inside a persistent Windows Sandbox using the winapp CLI --sandbox option. -->
# Windows Sandbox execution

Build on your machine, then run and automate the app inside a persistent Windows Sandbox.

> [!NOTE]
> This feature is in development. `winapp run --sandbox`, `winapp unregister --sandbox`,
> `winapp ui ... --sandbox`, `winapp sandbox exec`, and `winapp sandbox cp` work. This page
> documents the behaviour and its failure modes so they are reviewable alongside the code.

## Why

Windows UI automation normally runs on your own desktop. An automated workflow can steal focus,
move your cursor, type into the wrong window, or simply require you to stop using the machine
while it runs.

Sandbox execution moves the application and its automation into an isolated Windows session,
while builds stay on the host and stay fast.

```powershell
winapp run . --sandbox
winapp ui inspect --sandbox -a MyApp
winapp ui invoke --sandbox SubmitButton -a MyApp
```

The Sandbox stays running between commands and between rebuilds, so a rerun transfers only what
changed.

## What `--sandbox` does and does not protect

`--sandbox` isolates the **running** application. It does **not** make an untrusted project safe
to open: project evaluation, package restore, and compilation all happen on the host.

Everything inside the Sandbox is one trust boundary. Applications and workflows sharing it also
share the user account, the desktop, registry and package state, installed runtimes, and network
access, and can observe or interfere with one another. Isolating two workflows from each other
needs two machines.

The host-to-guest connection is treated as untrusted regardless: every boot gets a fresh identity
and secret, the guest serves commands only after an authenticated and encrypted handshake, requests
carry structured argument arrays rather than interpolated command lines, and every path is
canonicalized and confined to a managed root.

### What path containment does and does not guarantee

Paths crossing into the guest are canonicalized, confined to a managed root, and refused if any
component is a reparse point — a junction or symbolic link. Managed folders are never enumerated
through one either, so a link cannot make content outside a managed root appear to be inside it.

The same rule applies on the **host** side, to the folder a deployment or `sandbox cp` reads from.
Those folders are walked one level at a time with every directory tested before it is descended
into, rather than with a recursive enumeration that would follow a junction. A file-level check
alone would not be enough there: a file reached *through* a directory junction is an ordinary file
and carries no reparse attribute, so `build\logs` pointing at `C:\Users\you\.ssh` would otherwise be
hashed and copied into the guest as `build\logs\id_rsa`. A junction that points back at its own
ancestor ends the walk for the same reason, instead of recursing until the path length gives out.
Deployment refuses such a folder outright; `sandbox cp` treats the link as absent and copies only
what is genuinely inside the folder you named.

That reliably stops a link that **already exists** in the path, which is how one realistically
appears: left behind by an earlier deployment, an application, or an extracted archive. Each
ancestor is also re-checked immediately before a file is opened for hashing or copying, so the
unguarded window is the open itself rather than the whole enumerate-then-read pass.

It is **not** proof against a co-resident guest process that replaces a verified directory with a
junction in the moment between the check and the write. Closing that race requires handle-relative,
no-follow file opens, which v1 does not implement.

That residual race is accepted deliberately, and it is consistent with the trust model above rather
than an exception to it: a guest process able to win it can already terminate the agent, edit the
deployment directly, or interfere with the application, because everything in the Sandbox runs as
the same user. It is not the weakest link. Workflows that must be isolated from one another need
separate machines.

## Running an app

```powershell
winapp run . --sandbox
winapp run .\MyApp.csproj --sandbox --detach --json
winapp run .\publish --sandbox --clean
```

The app is built and its package layout is produced on the host, exactly as a local run would
produce it. Nothing is registered on your machine and no runtime is installed on it: the layout is
transferred into the Sandbox, and guest `winapp` registers, launches, and — with `--debug-output` —
debugs it there.

Every existing run option keeps its meaning, because the guest runs the same `winapp run` you would
have run locally:

| Option | In Sandbox |
|---|---|
| `--detach` | Returns after the guest launch; the app and the Sandbox keep running |
| `--debug-output` | Debugs inside the guest and streams its output back |
| `--no-launch` | Deploys and registers in the Sandbox without launching |
| `--clean` | Clears that guest package's application data, and redeploys from scratch |
| `--unregister-on-exit` | Unregisters that guest package once its process exits; the Sandbox stays |
| `--with-alias` | Launches the guest execution alias with forwarded standard streams |
| `--json` | stdout stays machine-readable; progress goes to stderr |

Build options — `--configuration`, `--arch`, `--framework`, `--property`, `--no-build`,
`--no-restore` — still apply on the host, before anything is transferred.

Unpackaged apps run too: there is no package to register, so the build output is deployed and the
app's executable is started in the guest. Its working directory is the deployed folder, because the
host directory you ran from does not exist there. `--debug-output` is not available for an
unpackaged app in Sandbox and is refused up front rather than silently producing nothing.

Under `--json`, Sandbox runs add fields to the existing document and change none of the existing
ones:

```json
{
  "AUMID": "Contoso.MyApp_8wekyb3d8bbwe!App",
  "ProcessId": 4212,
  "Sandbox": true,
  "ProcessScope": "sandbox",
  "AppTarget": "sandbox:4212",
  "ExecutionTarget": {
    "Kind": "windows-sandbox",
    "Id": "windows-sandbox:default",
    "Architecture": "arm64",
    "Epoch": "..."
  }
}
```

`ProcessId` is a **guest** process ID and is meaningful only within `ExecutionTarget.Epoch`. A value
from a previous generation is rejected rather than resolved against whatever Sandbox exists now.

## Removing an app

```powershell
winapp unregister --sandbox
winapp unregister --sandbox --manifest .\Package.appxmanifest
```

This removes only the package the matching deployment registered. Ownership has to hold twice
before anything is removed: winapp's own record must say that deployment registered this identity in
the current Sandbox generation, and the guest must then confirm the registration is a development
package rooted in that deployment's managed folder. A package you installed in the Sandbox yourself
satisfies neither and is never touched.

## Automating the UI

```powershell
winapp ui inspect --sandbox -a MyApp
winapp ui invoke --sandbox SubmitButton -a MyApp
winapp ui screenshot --sandbox -a MyApp -o .\result.png
winapp ui record --sandbox -a MyApp --duration-sec 5 -o .\result.mp4
```

Every `ui` verb accepts `--sandbox`. The command is intercepted once, before any local UI service
runs, and forwarded whole to guest winapp — so the host performs no UI Automation, window discovery,
capture, or input injection. That is the point: a Sandbox workflow cannot steal your focus, move your
cursor, or type into your windows.

A string app target can opt in by prefix instead:

```powershell
winapp ui inspect -a sandbox:MyApp
winapp ui inspect -a sandbox:4212
```

A numeric `--window` is left alone and needs `--sandbox`, because a window handle carries no scope of
its own — inferring one would resolve a host window against the guest, or the reverse.

Targeting is unchanged: if neither `--app` nor `--window` is given, the command fails and lists guest
targets rather than guessing. `winapp ui list-windows --sandbox` is the discovery path.

Guest process IDs and window handles are valid only inside the execution target's current epoch, and
values from a previous generation are rejected rather than resolved against a recreated Sandbox.

### Output files

`-o/--output` is redirected into per-command guest staging, and the file is brought back after the
command succeeds. It reaches the path you asked for only after its size and hash match what the guest
reported, and it is published by rename — so an interrupted transfer never leaves a shorter but
plausible screenshot or a truncated video where a complete one is expected, and never overwrites what
was already there. A failure names the artifact, its expected size, how much arrived, and the phase
that failed.

Transfers restart rather than resume. The result the command prints reports your path, not the guest
staging path it was actually written to.

## Requirements

- Windows 11 24H2 or newer, on a supported edition
- Hardware virtualization enabled
- The Windows Sandbox optional feature installed
- A compatible `wsb.exe`
- An unlocked interactive host session, while a command needs real input or screen capture

winapp does not enable Windows features, change firmware settings, or reboot. Missing prerequisites
fail **before** your application is built, and there is no silent fallback to running locally — a
command that asked for Sandbox either runs there or fails.

## Lifecycle

Windows permits one Sandbox at a time, and winapp is deliberately conservative about it.

winapp records the exact instance it created plus a random per-boot value. If a Sandbox is running
that winapp cannot prove it created, the command reports the running ID and stops. It is never
adopted and never terminated, because it may hold work you care about. A `wsb stop --id ...` command
is offered as advisory guidance only.

You manage the Sandbox with the Windows Sandbox CLI:

```powershell
wsb list
wsb connect --id <id>
wsb stop --id <id>
```

winapp does not shut the Sandbox down automatically.

If you close the Sandbox or run `wsb stop` while commands are running, they fail with
`sandbox_terminated`. Process IDs, window handles, deployments, and package records from the old
generation are invalidated rather than resolved against whatever is created next.

### The Sandbox window must stay connected

Real input and screen recording require the Sandbox's remote-session client to be connected and
not minimized. winapp keeps the window it owns off-screen and at the bottom of the window order,
without activating it, so it never takes your foreground.

Closing that window has a specific and initially surprising effect, established by measurement on
Windows 11 ARM64:

| Capability | Client connected | Client closed |
|---|---|---|
| Guest session and running apps | works | works |
| UI Automation inspection and UIA-pattern actions | works | works |
| Real input (`SendInput`, mouse) | works | **fails** |
| Windows Graphics Capture recording | works | **fails** |

So inspection keeps working in exactly the state where input must be refused. winapp reports that
as `sandbox_input_not_ready` rather than reporting input it did not deliver. Reconnecting with
`wsb connect` restores the same guest session, the same running applications, and both capabilities.

## Shared runtimes

Before anything is deployed, winapp works out what the app needs at runtime and makes sure the
Sandbox has it. Requirements are read from what the build already produced, not re-derived: framework
dependencies declared in the package manifest, the exact Windows App SDK package recorded in an
unpackaged app's `*.deps.json`, and shared .NET frameworks named in `*.runtimeconfig.json`. These are
the same artifacts registration and apphost startup consume.

They are treated as **compatible constraints** — "this package, at this version or newer" — rather
than an exact machine snapshot. That is what lets winapp leave a runtime another app in the same
Sandbox is already using exactly as it is.

Payloads come from your caches first. The Windows App Runtime packages you already restored to build
the app are staged into the guest over the same verified file channel the app itself uses, so a
framework-dependent app no longer needs guest network access on first run. Only when no cached
payload satisfies the constraint does winapp acquire one, through the same download path
`winapp restore` uses, and cache it on the host.

A Windows App Runtime dependency resolves to the **whole runtime**, not just the package a manifest
names. A manifest declares only the Framework, but a WinUI app also needs the DDLM that lets an
unpackaged process find the runtime, plus Main and Singleton. winapp takes the complete inventory
from the cached runtime whose Framework satisfies the constraint, reads each package's real identity
from its own manifest — the DDLM's name and the Singleton's version do not follow from the
Framework's — and stages, installs, and verifies all of them.

Unpackaged Windows App SDK builds declare no package dependency. For those, winapp reads the exact
`Microsoft.WindowsAppSDK` version from the build's `*.deps.json` and selects that version's cached
runtime inventory, so the bootstrapper finds the same Framework and DDLM the app was built against.

Shared .NET runtimes are provisioned too. winapp builds a portable layout from an official payload
it already has: a .NET installation on your machine, or the
`Microsoft.NETCore.App.Runtime.win-{arch}` and `Microsoft.WindowsDesktop.App.Runtime.win-{arch}`
runtime packs in your NuGet cache, restoring the exact matching pack when neither is present. The
layout is cached on the host, staged into the guest, and unpacked side-by-side into a per-user .NET
root winapp owns under the guest's managed folder. Nothing machine-wide is touched, no elevation is
taken, and the app is launched with `DOTNET_ROOT` pointing at that root — but only when the managed
root is what actually satisfies a framework. A Sandbox that already has the runtime is left alone.

`DOTNET_ROOT` is exclusive: an apphost pointed at a root resolves every framework from there and
consults nothing else. So it is all from one root or none from it. A guest that can serve part of
the graph but not all of it gets the whole graph installed into the managed root.

A desktop app's runtime configuration names only `Microsoft.WindowsDesktop.App`, which cannot load
without `Microsoft.NETCore.App` beneath it, so the core runtime is provisioned with it. Version
matching follows .NET's own roll-forward: a newer patch or minor of the same major is compatible, a
different major is not.

Identity is matched the way Windows matches it. A requirement carries the publisher and the exact
architecture, and only a registered package with the same name and publisher, at or above the
required version, in that architecture or genuinely neutral, satisfies it. An x86 package never
satisfies an x64 dependency.

Installation happens in the guest, under the same lock every other guest mutation takes, and is
journaled before the first package is touched. A package already registered at or above the required
version is skipped, and a shared framework already present is not unpacked over. Nothing is ever
removed or downgraded, and nothing is installed on your machine. Each version is published by moving
a fully unpacked staging folder into place, so an interrupted install leaves disposable temporary
content rather than a half-populated runtime.

Afterwards the whole required graph is re-read and verified. If it cannot be satisfied, the command
fails with `sandbox_runtime_provision_failed` naming the requirement that is missing — before the
app is deployed and launched, rather than as an unexplained startup failure:

```text
Windows Sandbox is missing a runtime the app requires: Microsoft.WindowsDesktop.App 10.0.0 or newer.
```

One dependency is fetched rather than found: the desktop VC runtime
(`Microsoft.VCLibs.140.00.UWPDesktop`) ships in no package a build restores and is not in the
Windows SDK, so winapp downloads it from its one official Microsoft address when no host copy
exists. The downloaded bytes are staged, never written straight to the cache, and must clear two
gates before they are published. First the staged file must carry a valid Authenticode signature
that chains to a trusted root and is signed by Microsoft; then its manifest identity, version,
architecture, and publisher must match what was asked for. The signature comes first because
everything the identity check reads lives inside the downloaded package and can be written to say
anything, so identity alone proves only that a package *claims* to be Microsoft's. A failure at
either gate deletes the staged file and publishes nothing, so a rejected payload never becomes a
host cache entry that a later run — or a later guest — would trust without re-deriving it. The
allowlist is a closed list of known packages, not a rule about names. Any other dependency with no
available payload is verified in the guest instead; if the Sandbox already has it, the run proceeds.

The complete graph is verified before **every** launch, not just the first. A clean provisioning
record says what winapp did, not what the guest currently has — `sandbox exec` gives any caller a
way to change package and runtime state inside the same Sandbox generation. Re-verification is
cheap: payloads the guest already holds are not re-transferred, and nothing already satisfied is
reinstalled. The record's job is narrower — it says whether a previous pass was interrupted, and
therefore whether the staged copy has to be rebuilt from scratch.

## Deployment

Each resolved input gets an internal deployment identity derived from its canonical path and, when
present, its original package identity. It scopes guest folders, state, ownership, and artifacts. It
is not a public target and is never guessed from the current directory.

After the host build, winapp captures an immutable snapshot of the desired layout — relative paths,
sizes, timestamps, and content hashes. If files change while the snapshot is being taken, deployment
aborts and asks for a rebuild rather than shipping a mixture of two builds.

A rerun then reconciles exactly: the deployment is marked dirty and the desired state persisted
*before* any file moves, changed and added files are transferred and hash-verified, files absent
from the desired state are deleted, the whole layout is re-compared, and only then is the deployment
marked clean.

Two consequences are worth knowing:

- A file you deleted from your build output is deleted in the guest. Leaving it would let a rerun
  keep executing code you just removed.
- A deployment that was interrupted never launches. The next run redeploys it completely. There is
  no partial-success state that reports healthy.

Package registration preserves per-user application state by default. `run --clean` is the explicit
clean reinstall, and it clears only that deployment's own state.

winapp unregisters only a package whose registered location matches the deployment, and confirms it
is a development-mode registration before removing it. A package you installed yourself is never
adopted or removed, and a provisioned or inbox package that blocks development registration is
reported with its exact identity rather than removed.

## Guest agent

A hidden mode of the architecture-matched `winapp.exe` runs as a persistent agent inside the
Sandbox. It is not a public command.

The agent runs ordinary guest `winapp` child commands for run, unregister, debugging, and UI
automation — it does not reimplement them. That is what keeps a command's behaviour identical
whether you typed it locally or routed it through Sandbox.

At startup it verifies it is not in session 0 and that its window station and input desktop are
interactive. It publishes its status **whether or not it is ready**, so a disconnected Sandbox
window is reported as exactly that rather than as a timeout.

Every command runs inside a Job Object so that cancelling it terminates the whole process tree, not
just the process winapp started. Because Windows cannot create a process that is already a job
member, the agent starts a small internal barrier instead: it waits until the agent has placed it in
the job, and only then starts the requested command — which Windows puts in the job at creation,
because its parent is already a member. There is no window in which a spawned descendant could
escape its operation's cancellation.

The barrier's release signal is per-operation and randomly named, but it is **not a secret**: the
name appears on the barrier process's command line, which any same-user process can read. Randomness
only raises the bar against blind guessing. As with path containment above, a co-resident process
able to exploit that can already terminate the agent outright, so it is accepted under the same
mutually-trusted model rather than defended against.

Host and guest `winapp` are versioned together. When the host is newer, the replacement binary is
staged, hash-verified, self-tested in its own process, and activated only if it passes, with the
previous binary retained as last-known-good. A newer guest is never downgraded: it is reused when
protocol versions overlap, and reported incompatible when they do not — in which case updating the
host is the fix.

## Coordination between commands

There is no background winapp service. Concurrent winapp processes coordinate through a per-target
lock, atomic revisioned state files, and a generation identity carried on every request and result.

The lock covers Sandbox creation and repair, guest agent replacement, runtime installation,
deployment synchronization, and package registration. It deliberately does **not** cover host
builds, running applications, or read-only UI Automation — so a long build or a running app never
blocks another workflow, and an inspection never waits behind a deployment.

If a winapp process dies mid-change, the next one treats the abandoned lock as a recovery signal and
reconciles before mutating further.

This lock is unrelated to UI turn coordination. It protects guest environment and deployment state,
not the desktop.

## Failures

Sandbox failures extend the invoking command's existing error contract. Routing through Sandbox
never moves a command's success or error payload between stdout and stderr, and progress messages
never mix into machine-readable output.

```json
{
  "error": {
    "code": "sandbox_unmanaged_instance",
    "message": "Another Windows Sandbox instance is already running.",
    "context": { "sandboxId": "..." },
    "userAction": "Close the existing Sandbox if it is safe to do so, then retry.",
    "nextCommand": { "command": "wsb stop --id ...", "advisory": true },
    "example": "winapp run . --sandbox"
  }
}
```

A `nextCommand` marked `advisory` needs your judgement and is never run automatically.

Infrastructure failures use codes distinct from your application's exit codes, so "winapp could not
run your app" is always distinguishable from "your app failed".

| Code | Meaning |
|---|---|
| `sandbox_unsupported` | This host cannot run Sandbox at all |
| `sandbox_unmanaged_instance` | A Sandbox winapp does not own is running |
| `sandbox_start_failed` | Creating or starting the managed Sandbox failed |
| `sandbox_no_interactive_session` | No interactive guest session |
| `sandbox_input_not_ready` | Input could not be delivered; nothing was reported as delivered |
| `sandbox_terminated` | The Sandbox went away underneath the command |
| `sandbox_agent_incompatible` | The guest agent needs a newer winapp |
| `sandbox_agent_upgrade_failed` | Staging, self-testing, or activating a replacement agent failed |
| `sandbox_transport_failed` | The command channel could not be established or was lost |
| `sandbox_transfer_interrupted` | A transfer stopped; no destination was published |
| `sandbox_runtime_provision_failed` | A required runtime could not be provisioned |
| `sandbox_deployment_dirty` | The guest copy is incomplete; it will not launch |
| `sandbox_package_conflict` | Another deployment owns this package identity |
| `sandbox_provisioned_package_conflict` | A provisioned package blocks registration |
| `sandbox_target_ambiguous` | The command did not identify exactly one target |
| `sandbox_target_stale` | State refers to a Sandbox that no longer exists |
| `sandbox_stale_handle` | A process ID or window handle from a previous generation |
| `sandbox_artifact_failed` | Producing, verifying, or publishing an output failed |

## Architecture
Windows Sandbox is the only public target. Internally it sits behind a narrow boundary so a future
Hyper-V, Dev Box, or remote-machine target can reuse everything above it without a rewrite.

```text
run / ui / unregister / sandbox exec / sandbox cp
        │
        ▼
ExecutionTargetOrchestrator      probe → lock (only if mutating) → connect → negotiate
        │
        ├── TargetDeploymentService     snapshot, reconcile, ownership
        │
        ▼
GuestCommandChannel              one target-neutral protocol
        │  IGuestTransport
        ▼
WindowsSandboxBackend            the only code that knows Sandbox exists
        │  wsb start / list / share / connect / exec / stop
        ▼
Windows Sandbox  ──►  guest winapp agent  ──►  guest winapp child commands
```

Only the backend may invoke `wsb.exe` or touch the Sandbox window. Deployment, runtime provisioning,
UI forwarding, and artifact handling sit above the boundary and never reference Sandbox APIs, paths,
or IDs. That separation is enforced by tests rather than convention: the orchestration test suite
runs the real host channel against the real guest server over an in-memory transport, so any
dependency on a `wsb` command would make those tests impossible to run.

`wsb exec` is used for exactly two things — launching the agent and enabling the guest development
prerequisite — because it takes the command as a
single string and returns only an exit code. It can carry neither argument boundaries nor guest
output, which is why real work goes over the authenticated channel and why the agent writes its own
startup diagnostics to a bounded, guest-writable folder the host reads once and removes.

### Telemetry

`winapp sandbox exec` and `winapp sandbox cp` never contribute executable or argument values,
environment variables, host or guest paths, stream contents, or file names to telemetry. String,
file, and directory values are recorded as a constant placeholder rather than their content, and
tests pin that so a future change to the redaction rule cannot silently start collecting them.

### Live coverage

Almost everything is verified without a Sandbox: the real host channel runs against the real guest
server over an in-memory transport, so a dependency on a `wsb` command would make those tests
impossible to run at all. The tests that do need a real machine are gated on
`WINAPP_SANDBOX_E2E=1` and `WINAPP_SANDBOX_E2E_BINARY`, which must point to the
architecture-matched NativeAOT `winapp.exe` produced under `artifacts\cli\win-x64` or
`artifacts\cli\win-arm64`. Windows permits one Sandbox at a time and creating one is a machine-wide,
visible side effect. The tests stop only an instance they created, and skip rather than fail when an
unowned one is already running.

## See also

- [UI automation](ui-automation.md)
- [Debugging with package identity](debugging.md)
- [Security guidance](security.md)
