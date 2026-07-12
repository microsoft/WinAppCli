# Copyright (c) Microsoft Corporation. Licensed under the MIT License.
#
# validate-schema.py — the JSON-Schema (draft 2020-12) guard for the WDXP canonical file.
#
# This is the *second* validator (the C# conformance suite is the first): it checks that
# wdxp.v0.json still satisfies wdxp.schema.json structurally. Keeping both green closes the
# "two validators drift apart" gap. Pure-stdlib except for `jsonschema`; no network access —
# every $ref in the schema is internal (#/$defs/...), and the instance's own "$schema":
# "./wdxp.schema.json" is treated as plain data, never fetched.
#
# Usage:  python3 protocol/scripts/validate-schema.py
# Exit 0 = valid; 2 = invalid / error.

import json
import sys
from pathlib import Path

try:
    from jsonschema import Draft202012Validator
except ImportError:
    print("error: the 'jsonschema' package is required (pip install jsonschema)", file=sys.stderr)
    sys.exit(2)

PROTOCOL_DIR = Path(__file__).resolve().parent.parent
SCHEMA_PATH = PROTOCOL_DIR / "wdxp.schema.json"
INSTANCE_PATH = PROTOCOL_DIR / "wdxp.v0.json"


def load(path: Path):
    try:
        with path.open(encoding="utf-8") as fh:
            return json.load(fh)
    except FileNotFoundError:
        print(f"error: file not found: {path}", file=sys.stderr)
        sys.exit(2)
    except json.JSONDecodeError as exc:
        print(f"error: {path.name} is not valid JSON: {exc}", file=sys.stderr)
        sys.exit(2)


def main() -> int:
    schema = load(SCHEMA_PATH)
    instance = load(INSTANCE_PATH)

    # Validate the schema document itself, then the canonical instance against it.
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema)
    errors = sorted(validator.iter_errors(instance), key=lambda e: list(e.path))

    if errors:
        print(f"FAIL: {INSTANCE_PATH.name} does not satisfy {SCHEMA_PATH.name} "
              f"({len(errors)} error(s)):", file=sys.stderr)
        for err in errors:
            location = "/".join(str(p) for p in err.path) or "<root>"
            print(f"  - at {location}: {err.message}", file=sys.stderr)
        return 2

    print(f"PASS: {INSTANCE_PATH.name} satisfies {SCHEMA_PATH.name} "
          f"(draft 2020-12; root closed, internal $refs only).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
