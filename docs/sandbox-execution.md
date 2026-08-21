<!-- mslearn: true -->
<!-- ms.topic: concept-article -->
<!-- description: Run, debug, and UI-automate Windows applications inside a persistent Windows Sandbox using the winapp CLI --sandbox option. -->
# Windows Sandbox execution

Build on your machine, then run and automate the app inside a persistent Windows Sandbox.

> [!NOTE]
> This feature is in development. The internal execution-target layers described under
> [Architecture](#architecture) have landed; the `--sandbox` option and the `winapp sandbox`
> commands are not available yet. This page documents the design being implemented so the
> behaviour and its failure modes are reviewable alongside the code.

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

That reliably stops a link that **already exists** in the path, which is how one realistically
appears: left behind by an earlier deployment, an application, or an extracted archive.

It is **not** proof against a co-resident guest process that replaces a verified directory with a
junction in the moment between the check and the write. Closing that race requires handle-relative,
no-follow file opens, which v1 does not implement.

That residual race is accepted deliberately, and it is consistent with the trust model above rather
than an exception to it: a guest process able to win it can already terminate the agent, edit the
deployment directly, or interfere with the application, because everything in the Sandbox runs as
the same user. It is not the weakest link. Workflows that must be isolated from one another need
separate machines.

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

winapp unregisters only a package whose recorded full name **and** registered location match the
deployment. A package you installed yourself is never adopted or removed, and a provisioned or inbox
package that blocks development registration is reported with its exact identity rather than removed.

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

`wsb exec` is used for exactly one thing — launching the agent — because it takes the command as a
single string and returns only an exit code. It can carry neither argument boundaries nor guest
output, which is why real work goes over the authenticated channel and why the agent writes its own
startup diagnostics to a bounded, guest-writable folder the host reads once and removes.

## See also

- [UI automation](ui-automation.md)
- [Debugging with package identity](debugging.md)
- [Security guidance](security.md)
