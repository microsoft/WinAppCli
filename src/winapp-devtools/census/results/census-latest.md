# Source-resolution census (Gate 1)

_Generated 2026-07-12 05:54:56Z · pages: Items, Repeater, SmokePage, UcHost, XBindFn_

**Verdict: GO** — GO: source-backed resolved 100% ≥ 70% and templated-to-template 58.3% ≥ 40% in 'release', 0 false-confident.

## Gate-1 metrics

| Config | Total | Source-backed→line % | Templated→template % | False-confident % |
|---|--:|--:|--:|--:|
| debug | 580 | 100 | 58.3 | 0 |
| release | 580 | 100 | 58.3 | 0 |
| release-nolineinfo | 580 | 0 | 58.3 | 0 |

## Elements by SourceKind

| Config | Total | source-backed | template-generated | style-generated | binding-generated | runtime-only | resource-origin | ambiguous | unreachable |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| debug | 580 | 160 | 29 | 216 | 0 | 175 | 0 | 0 | 0 |
| release | 580 | 160 | 29 | 216 | 0 | 175 | 0 | 0 | 0 |
| release-nolineinfo | 580 | 160 | 29 | 216 | 0 | 175 | 0 | 0 | 0 |

Grades come from the source-provenance grader (spec §4): **source-backed** = the app's own authored page/UserControl markup (the only kind allowed an exact line); **template/style-generated** = mapped to a control-template or theme/style definition, never the page; **runtime-only** = no markup provenance. *Source-backed→line %* is the select-to-source floor; *Templated→template %* is the fraction of generated elements that still map to a template/style source; *False-confident %* must be 0.

## Per page × config (raw)

| Config | Page | OK | Elements | TSV |
|---|---|:--:|--:|---|
| debug | Items | ✅ | 247 | debug-Items.tsv |
| debug | Repeater | ✅ | 129 | debug-Repeater.tsv |
| debug | SmokePage | ✅ | 100 | debug-SmokePage.tsv |
| debug | UcHost | ✅ | 71 | debug-UcHost.tsv |
| debug | XBindFn | ✅ | 33 | debug-XBindFn.tsv |
| release | Items | ✅ | 247 | release-Items.tsv |
| release | Repeater | ✅ | 129 | release-Repeater.tsv |
| release | SmokePage | ✅ | 100 | release-SmokePage.tsv |
| release | UcHost | ✅ | 71 | release-UcHost.tsv |
| release | XBindFn | ✅ | 33 | release-XBindFn.tsv |
| release-nolineinfo | Items | ✅ | 247 | release-nolineinfo-Items.tsv |
| release-nolineinfo | Repeater | ✅ | 129 | release-nolineinfo-Repeater.tsv |
| release-nolineinfo | SmokePage | ✅ | 100 | release-nolineinfo-SmokePage.tsv |
| release-nolineinfo | UcHost | ✅ | 71 | release-nolineinfo-UcHost.tsv |
| release-nolineinfo | XBindFn | ✅ | 33 | release-nolineinfo-XBindFn.tsv |

