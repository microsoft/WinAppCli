<!--
Copyright (c) Microsoft Corporation. Licensed under the MIT License.
-->
# WDXP envelope — the normative framing spec

This is the wire contract that carries every WDXP domain. The domains (methods, events, types)
live in [`wdxp.v0.json`](./wdxp.v0.json); this document specifies how those messages are framed,
routed, negotiated, cancelled, and how errors are reported. It is **normative**: a conformant engine
or client MUST follow it.

It is grounded in the proven substrate, not invented: the round-trip below is exactly what
the proven transport already demonstrates — newline-delimited JSON-RPC 2.0 with a
request/response pair **and** a server-initiated notification over a `CurrentUserOnly` named pipe.

---

## 1. Transport

- **Named pipe**, per target: `wdxp-<targetPid>` (the injected engine is the server; clients connect).
- **`PipeOptions.CurrentUserOnly`** — the OS enforces same-user access. This is the first line of the
  security model (see §7); it is not optional, because the channel can trigger live app mutation.
- **UTF-8, no BOM.** One JSON-RPC message per line; `\n` terminates a message. Messages MUST NOT
  contain a raw newline (JSON string escaping handles embedded newlines). No `Content-Length` framing —
  the delimiter is the newline (this is the proof-of-concept-proven framing, and it keeps the CLI trivially
  scriptable with line-oriented tools).

## 2. Message shapes (JSON-RPC 2.0)

Three message kinds, all with `"jsonrpc": "2.0"`:

**Request** (client → engine, or engine → client for reverse calls like `Selection.pick`):
```json
{ "jsonrpc": "2.0", "id": "42", "method": "VisualTree.search", "params": { "name": "Title" } }
```
- `id` is a string or number, unique per connection while in flight. A request always gets exactly one
  response with the same `id`.
- `method` is `"<Domain>.<command>"` (dot-qualified, matching `wdxp.v0.json`).
- `params` is an **object** keyed by the parameter `name`s in the schema (named params, not positional).
  Omit `params` for zero-parameter commands.

**Response** (success):
```json
{ "jsonrpc": "2.0", "id": "42", "result": { "matches": [ { "handle": 12, "generation": 1 } ] } }
```
- `result` is an **object** keyed by the command's `returns` field `name`s. A command with an empty
  `returns` still returns `"result": {}` on success.

**Response** (error) — see §6:
```json
{ "jsonrpc": "2.0", "id": "42", "error": { "code": -32001, "name": "StaleHandle", "message": "..." } }
```

**Notification** (engine → client, no `id`, no response):
```json
{ "jsonrpc": "2.0", "method": "VisualTree.childrenChanged",
  "params": { "node": { "handle": 3, "generation": 2 }, "added": [], "removed": [] } }
```
- `method` is `"<Domain>.<event>"`. Events are one-way; a client subscribes via the domain's
  `subscribe` command and receives these until it `unsubscribe`s or the session closes.

## 3. Session = connection

There is **no separate session object**: one pipe connection is one session. `Target.attach` binds the
connection to exactly one target, so "which app" is the connection, never a per-call argument. This is
why no command takes a target/window selector the way `winapp ui` does with `-a`/`-w`.

- A connection begins **unattached**. Only `WDXP.*` and `Target.list`/`Target.attach` (and
  `Security.authenticate`) are valid before attach.
- `Target.attach` performs **loud-fail-before-unsafe-attach**: on version mismatch, missing capability,
  or a missing required runtime component, it returns an error and leaves the connection unattached — it never
  half-attaches.
- `Target.reconnect` re-binds a dropped transport to a still-living target, preserving session identity
  (the proof-of-concept regression oracle: a handle from op-1 must resolve at op-20, including across a reconnect).

## 4. Capability negotiation

`WDXP.negotiate` is the first call on a session. The client sends the capabilities and max versions it
understands; the engine replies with the **intersection** it will honor. Thereafter:

- Calling a command in a **non-negotiated** capability returns `CapabilityUnsupported` (-32003) — a
  clean, typed refusal, **never** a crash or a silent no-op.
- Capabilities are **versioned independently** (`{ "name": "visualtree", "version": "0.1.0" }`). A domain
  may advance its version without bumping the whole protocol, so surfaces upgrade piecemeal.
- `stability` (`stable` | `experimental`) travels with each capability so a client can refuse to bind an
  experimental family in production.

The negotiated set is also the **authorization surface**: `Security.grant` grants by capability family,
so negotiation and consent share one vocabulary.

## 5. Cancellation

`WDXP.cancel { "id": "<in-flight id>" }` requests best-effort cancellation. Long reads (deep
`enumerate`) and applies (`HotReload.commit`) MUST check for cancellation at safe points and, if
cancelled, complete the original request with error `Cancelled` (-32008). Cancellation never leaves the
app in a torn state: an apply either reaches a defined `TransactionState` or rolls back.

## 6. Error taxonomy

Errors are **structured and typed**, not free text — agents branch on `code`/`name`. The `error` object
is `{ code, name, message, data? }`. Codes are fixed in `wdxp.v0.json` (`errorCodes`); the canonical set:

| Range | Meaning |
|---|---|
| `-32700 … -32603` | Standard JSON-RPC (parse / invalid request / method-not-found / invalid-params / internal). |
| `-32000 … -32008` | WDXP application errors. |

WDXP application errors and how a client should react:

| code | name | recoverable | client action |
|---|---|:--:|---|
| -32000 | `TargetLost` | no | The app exited; re-`attach`. |
| -32001 | `StaleHandle` | yes | Re-`enumerate`; handles carry a fresh `generation`. |
| -32002 | `NotOnDispatcher` | yes | Engine bug — a mutation was routed off the UI dispatcher (the `RPC_E_WRONG_THREAD` analog). Clients should never see this; the engine MUST marshal. |
| -32003 | `CapabilityUnsupported` | yes | Negotiate the capability, or degrade. |
| -32004 | `Unauthorized` | yes | Request a grant (`Security.grant`) or re-`authenticate`. |
| -32005 | `RefusedUnsafe` | yes | The edit was classified unsafe to apply in place; fall back to scoped reload. |
| -32006 | `UnreachableGate` | yes | An apply gate (e.g. render verify) was not reached; retry or restart. |
| -32007 | `SourceUnavailable` | yes | No provenance for this element; use the `Anchor`. Expected for runtime-only/stripped elements — not a failure to fear. |
| -32008 | `Cancelled` | yes | The request was cancelled via `WDXP.cancel`. |

**Honesty invariant.** The engine MUST NOT report a stronger outcome than it achieved. A mutating
command reports one of the four `Outcome`s (`applied` / `applied-inert` / `reloaded` / `needs-restart`)
and, for transactions, a real `TransactionState` including the honest terminal-failure states
(`refused-unsafe`, `stale-handle`, `target-lost`, `unreachable-gate`). These are **results, not
exceptions** — they arrive as a normal `result`, not an `error`, because they are expected outcomes an
agent branches on.

## 7. Security posture (framing-level)

Full policy is the Security domain (W8); the envelope pins the parts that live at the wire:

- `CurrentUserOnly` pipe ACL + same-user process auth (§1).
- `Security.authenticate` establishes a **per-session nonce/token** with **replay prevention**; every
  command carries the session's authorization implicitly (it is the connection).
- Every command declares a **risk tier** (`read` / `mutate-ephemeral` / `structural` / `persist` /
  `privileged`). Tiers ≥ `persist` require **explicit** consent, not the session default.
- All non-`read` commands are written to a **tamper-evident local audit** (`Security.audit`).

## 8. Versioning & stability

- The whole file carries a SemVer `version`; each capability carries its own SemVer.
- `experimental` domains/commands may change shape between minor versions; `stable` ones may only add.
- **Additive rule (Gate 3):** adding a field/command/event to `wdxp.v0.json` MUST flow to every
  generated surface (CLI command-graph, docs) with **zero** hand edits. The conformance suite enforces
  this: every command and event must appear in every facade, and every declared field must appear in the
  CLI command-graph — so a field-add can never be silently dropped from the surface clients bind to. If a
  change needs a manual edit in more than one generated surface, the codegen — not the schema — is wrong.

---

*Reference: the proven transport. Generated facades: `protocol/gen`. Golden traces that exercise this
envelope: `protocol/golden`.*
