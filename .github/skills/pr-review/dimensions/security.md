# Security

Apply `_shared-contract.md`. Set `Domain: security`.

You know the standard vulnerability classes. What matters here is **where this
particular CLI is exposed**.

## This tool's attack surface

It launches Windows SDK build tools (`makeappx`, `signtool`, `makepri`,
`cppwinrt`) via `Process.Start`; generates and installs code-signing certificates
and PFX files; reads and writes `appxmanifest.xml`; downloads NuGet packages and
SDK tools over the network; registers sparse and loose-layout packages with
Windows; drives UI Automation against arbitrary running apps; and ships an npm
wrapper that shells out to the native binary.

The recurring shape of a real bug here is **a value from a manifest, a config
file, or a CLI arg reaching a process invocation or a file path unvalidated.**

## Escalations (mandatory minimums)

| Pattern | Minimum severity |
|---|---|
| `BinaryFormatter` / `SoapFormatter`, or JSON with `TypeNameHandling != None` | critical |
| `Process.Start` with unsanitized external input | high |
| Hardcoded credential (anything but the documented dev default `password`) | high |
| New HTTP listener bound to anything but loopback | high |
| Download over non-HTTPS, or from a non-Microsoft host with no checksum | high |
| Structured manifest XML edited by regex in new code | medium |
| Missing elevation check on a path that requires admin | medium |

Regex on manifest XML is allowed **only** for pre-parse placeholder replacement
(`$targetnametoken$`) on raw text before the XML is valid. Structured edits go
through `AppxManifestDocument`.

## Required: name the red-team attempt

For the highest-risk item you find, describe one concrete attempt the
orchestrator can run in the Validate phase — e.g. *"pass a manifest whose
`Source` is `a b\" --flag` and confirm the extra flag reaches makeappx."*
Findings stay `static-only` until that phase reproduces or refutes them.

This is the most valuable thing you produce: a security finding nobody can
reproduce gets ignored, and one that gets reproduced gets fixed.

## Reminders

- Security findings are **never** suppressed for low confidence. Emit them.
- If the dangerous sink is in the diff but the input source is not, use
  `Confidence: medium` and say so in `Show me`.
- Do not flag what the CA-series analyzers already catch.
