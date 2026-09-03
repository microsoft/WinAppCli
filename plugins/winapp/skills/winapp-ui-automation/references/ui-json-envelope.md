# `winapp ui --json` envelope (v0.3.1+)

The `--json` output for the `winapp ui` command group was reshaped in v0.3.1.
Generate parsers against these shapes — pre-0.3.1 parsers will silently break
because most fields were renamed, removed, or moved into envelopes.

## `ui inspect --json`

Top-level shape (elements are now nested under `windows[]`, not flat):

```json
{
  "depth": 0,
  "interactive": false,
  "hideDisabled": false,
  "hideOffscreen": false,
  "windows": [
    {
      "hwnd": "0x...",
      "title": "...",
      "className": "...",
      "elementCount": 0,
      "elements": [
        {
          "selector": "...",
          "name": "...",
          "controlType": "...",
          "children": [ ... ]
        }
      ]
    }
  ]
}
```

Pre-0.3.1 the shape was `{ "elements": [...] }`. Per-element `id`,
`parentSelector`, and `windowHandle` fields have been **removed** —
`selector` is the public handle.

## `ui inspect --ancestors --json`

Ancestors are now nested as a parent → child chain keyed by `Depth=i`
(previously emitted as sibling roots).

## `ui inspect --interactive`

Non-interactive ancestors are collapsed and surfaced as `ancestorPath` on
surviving descendants. `+more` markers indicate truncated subtrees in both
text and JSON modes.

## `ui get-focused --json`

Always emits an envelope (never a bare value):

- No focus: `{ "hasFocus": false }`
- With focus: `{ "hasFocus": true, "element": { ... } }`

Pre-0.3.1 emitted bare `null` when nothing was focused.

## `ui search --json` / `ui wait-for --json`

Both commands return matching elements using the same element shape as
`ui inspect` (so `selector`, `name`, `controlType`, `children`, etc.).
Each match may also include an `invokableAncestor` field — itself an
element-shaped object — pointing to the nearest parent that supports
`InvokePattern` (useful when a search hits a non-invokable element
like a label inside a button).

```json
[
  {
    "selector": "txt-save-label-a1b2",
    "name": "Save",
    "controlType": "Text",
    "children": [ ... ],
    "invokableAncestor": {
      "selector": "btn-save-c3d4",
      "name": "Save button",
      "controlType": "Button"
    }
  }
]
```

The internal `id`, `parentSelector`, and `windowHandle` fields are
**scrubbed** from results — both at the top level and inside any nested
`invokableAncestor`. Don't depend on them; use `selector` as the handle.

## Error envelope

Every `winapp ui` command writes errors to **stderr** as:

```json
{
  "error": {
    "code": "element_not_found",
    "message": "…",
    "selector": "btn-save-c3d4",
    "details": "…",
    "recoveryHint": "…"
  }
}
```

Only `code` and `message` are always present; the rest are omitted when
they do not apply.

### Desktop coordination

Concurrent `winapp ui` workflows take cooperative turns on the shared
desktop (see the skill's coordination section). Four additional codes can
appear:

| `code` | Meaning |
|---|---|
| `invalid_ui_workflow_id` | `WINAPP_UI_WORKFLOW_ID` is set but empty/whitespace or longer than 256 characters. Fails before any UI side effect. |
| `desktop_coordination_unavailable` | Coordination state could not be read, published, or safely rebuilt — including state written by a newer `winapp`. Mutating commands fail closed rather than acting uncoordinated. |
| `queue_capacity_exceeded` | 64 commands are already waiting for the desktop. |
| `cancelled` | Native Ctrl+C while the command was still waiting for its turn. The command never ran, so it has no UI side effects. Exit code **130**. |

An npm `AbortSignal` is a different contract: Node force-terminates the child,
so there is usually no envelope and no exit code 130 — the wrapper rejects with
an `AbortError` instead, and UI side effects may already have happened if the
abort landed after the command acquired the desktop.

`cancelled` — and optionally the other coordination errors — carries an
additive `coordination` object:

```json
{
  "error": {
    "code": "cancelled",
    "message": "UI turn wait was cancelled.",
    "coordination": {
      "waitedMs": 1234,
      "queuePosition": 2
    }
  }
}
```

`waitedMs` is always present for a cancellation while queued.
`queuePosition` is one-based among live waiters and is **omitted** when it
cannot be computed reliably — including while a command waits behind its
own workflow's earlier command. Workflow identities are never exposed, in
raw or hashed form.

Cancelling *after* the command acquired its turn keeps that command's
existing behavior; for example Ctrl+C during `ui record` still finalizes
the recording and returns its normal successful result.
