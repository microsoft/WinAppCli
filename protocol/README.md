# WDXP — the WinUI Design-time eXperience Protocol (v0)

**Workstream W2.** This folder is the **contract** the rest of the engine is built against: a
CDP-shaped, JSON-RPC-framed protocol for reading and mutating a **live WinUI 3 visual tree** from an
out-of-process client (a CLI, an IDE extension, an AI agent). W1 (daemon), W3 (read
surface), W5 (hot-reload), W7 (CLI client) and W9 (reference client) all implement or consume
what is defined here.

Status: **experimental / v0.1.0.** Shapes are stabilizing; the *policy* (families, outcomes, risk
tiers, provenance honesty) is settled by the nine-agent debate and is meant to hold.

---

## The one idea: author once, generate the rest (DAP-style)

There is exactly **one hand-authored source of truth** — [`wdxp.v0.json`](./wdxp.v0.json). Every
client-facing surface is **generated** from it. Nobody hand-maintains a second copy of the command
list.

```
                       wdxp.v0.json          ← the ONLY file humans edit
                    (canonical, machine-readable)
                            │
                 ┌──────────┴───────────┐
                 ▼                      ▼
           cli-commands           protocol-reference
              .json                     .md
           (W7 CLI)               (docs / humans)
```

This mirrors how **DAP** (`debugAdapterProtocol.json`) and **LSP** (`metaModel.json`) ship: a
language-neutral JSON contract that every implementation reads, with a small generator producing the
per-surface facades. We deliberately did **not** invent a DSL (CDP's PDL) or add a TypeSpec/Node
toolchain — the generator is zero-dependency .NET on the repo's pinned toolchain.

**Why this matters — Gate 3.** Adding a command or a field to the protocol must touch **≤ 1
hand-written surface** (the schema) and flow automatically into the CLI facade *and* the docs. The
conformance suite asserts this as `gate3-facade-totality`. If you find yourself editing the generator
to add a normal command, something is wrong — the generator walks the model generically.

---

## Files

| File | Role |
|---|---|
| **`wdxp.v0.json`** | **The canonical schema.** 10 domains · 32 commands · 8 events · 14 error codes · 5 risk tiers. Edit this; regenerate everything else. |
| `wdxp.schema.json` | JSON Schema (draft 2020-12) guard — authoring/IDE aid + structural validation of the canonical file. |
| `envelope.md` | **Normative framing spec** — transport, message shapes, session, negotiation, cancellation, error taxonomy, security, versioning. Read this to implement a client or the daemon. |
| `gen/` | The generator (`dotnet run` → emits the facades to `gen/out/`). `Model.cs` = loader + `$ref` resolver; `Program.cs` = validator + emitters. |
| `golden/*.json` | Golden message traces — the conformance oracle. Realistic request/response/event/error sequences a conformant peer must accept. |
| `conformance/` | The **W2 gate**. `dotnet run` → validates the schema, proves Gate-3 facade totality, and checks the golden traces. Exit 0 = green. |
| `gen/out/` | Generated facades (git-ignored — reproducible from the schema). |

---

## Workflow (verified commands)

```powershell
# 1. Author: edit wdxp.v0.json (add a domain / command / event / field).

# 2. Generate the facades (CLI + docs):
cd protocol/gen ; dotnet run -c Debug
#   -> gen/out/cli-commands.json, protocol-reference.md

# 3. Prove it (schema valid + Gate-3 totality + golden traces conform):
cd protocol/conformance ; dotnet run -c Debug
#   -> RESULT: PASS (5/5 checks)   [exit 0]
```

Both projects are pure `net10.0` (no `-windows` TFM, no WinAppSDK) so they build and the gate runs on
any hosted CI runner — this is a **fast gate**, not a heavy one.

---

## The family map (the 10 domains)

Grouped by the debate's guaranteed **floor** vs. the mutation/best-effort surfaces above it.

| Domain | Capability | Cmds/Evts | Stability | What it does |
|---|---|---|---|---|
| `WDXP` | `core` | 2 / 1 | stable | `negotiate` (capability handshake), `cancel`; `sessionEnded`. |
| `Target` | `target` | 4 / 2 | stable | Enumerate/attach/detach live app targets; attach/detach events. |
| `VisualTree` | `visualtree` | 5 / 1 | stable | **Floor.** Enumerate the tree, get children, resolve handles; `treeChanged`. |
| `Property` | `property` | 4 / 0 | stable | **Floor.** Read/write properties with a 7-value precedence `ValueSource`. |
| `Resource` | `resource` | 2 / 0 | stable | Resolve `{ThemeResource}` / `{StaticResource}` values. |
| `Source` | `source` | 2 / 0 | stable | **Confidence-graded** element→source mapping. Best-effort by design (see below). |
| `Diagnostics` | `diagnostics` | 2 / 1 | stable | Structured reason codes (incl. `release-no-line-info`); a diagnostics stream. |
| `HotReload` | `hotreload` | 5 / 1 | **experimental** | Transacted mutation with a 10-state `TransactionState` (incl. `refused-unsafe`). |
| `Selection` | `selection` | 3 / 1 | **experimental** | Out-of-proc select/overlay/highlight; selection events. |
| `Security` | `security` | 3 / 1 | **experimental** | Consent, risk-tier gating, audit; security events. |

**The floor vs. the flourish (a debate outcome you must not soften):** `VisualTree` + `Property`
reads are the **guaranteed floor** — every conformant client gets tree + property sight. `Source`
(select-to-source) is **demoted to confidence-graded best-effort**: source line-info is
compiler-emitted, `DisableXbfLineInfo`/env-gated, **stripped in Release**, and absent for
templated/virtualized/`x:Bind`-Fn elements. The Gate-1 source-resolution census confirmed this
empirically and it is baked into `Source.SourceSpan` (`uri` durable; `lineInfo:false` when line
info is unavailable) and `Diagnostics.ReasonCode`.

---

## The normative enums (the debate's crown jewels)

These carry the hard-won policy. Do not weaken them without re-opening the debate:

- **`Property.ValueSource`** — 7-value precedence (`local` → `animation` → `templateBinding` →
  `style` → `builtInStyle` → `inherited` → `default`). Clients must surface *where* a value came from.
- **`Source.SourceKind`** — 8 values including the honest `runtime-only` (no source backing).
- **`HotReload.TransactionState`** — 10 states including `refused-unsafe` (the engine may decline an
  unsafe apply) and the four applied/inert outcomes.
- **`Diagnostics.ReasonCode`** — structured, machine-actionable failure reasons.
- **Protocol-level `Outcome`** — the four-outcome classifier (`Applied` / `AppliedInert` /
  `ReloadRequired` / `NeedsRestart`) — the **honesty invariant**: the engine never claims success it
  can't guarantee.
- **Protocol-level `RiskTier`** — `read` / `mutate-ephemeral` / `structural` / `persist` /
  `privileged` — drives the `Security` consent gates.

---

## Framing, grounded in the proven substrate

The envelope is **not** new: it is the proven transport that already round-trips as
newline-delimited **JSON-RPC 2.0**
(requests/responses + server-initiated notifications) over a `PipeOptions.CurrentUserOnly` named
pipe. `envelope.md` is the normative write-up of that mechanism; the golden traces are expressed in
it. A **session is a connection**; capabilities are negotiated per-connection via `WDXP.negotiate`.

---

## Versioning & stability policy

- **Additive is safe:** new domains/commands/events/optional-fields bump the **minor** version and
  never break a negotiated client. This is the Gate-3 property in practice.
- **Breaking is loud:** removing/renaming a command, changing a field type, or making an optional
  field required bumps the **major** version and requires a new `WDXP.negotiate` capability entry.
- Per-domain `stability` (`stable` / `experimental`) tells clients what may still move. `HotReload`,
  `Selection`, `Security` are `experimental` in v0 on purpose.
- Full rules in [`envelope.md` §8](./envelope.md).

---

## Handoff notes for winapp-repo parallel agents

- **This is portable.** `wdxp.v0.json`, `envelope.md`, the golden traces, and the two `net10.0`
  projects move to the winapp CLI repo's `winui-devex` branch as-is. Nothing here depends on WinUI,
  Windows, or the TAP — it is the pure contract layer, provable on hosted CI.
- **Consume, don't re-derive.** If you are building:
  - **W1 (daemon):** implement `envelope.md` framing + `WDXP.negotiate`/`cancel`; treat the schema's
    capability list as the negotiation vocabulary.
  - **W3 (read surface):** implement the `VisualTree` + `Property` + `Resource` floor against the
    golden traces `02-read-floor.json`.
  - **W5 (hot-reload):** implement `HotReload` honoring `TransactionState` + the `Outcome` invariant;
    never report `Applied` for an inert change.
  - **W7 (CLI):** **generate** your surface from `gen/` — do not hand-write a command list.
    CLI path = `<capability> <kebab(command)>`.
  - **W8 (security):** the `Security` domain + `RiskTier` are your gate points.
- **The gate is real.** `protocol/conformance` is wired to be a required fast-gate check. Add a golden
  trace when you add a family; keep it green. A schema change that doesn't flow to every generated
  facade will fail `gate3-facade-totality`.
