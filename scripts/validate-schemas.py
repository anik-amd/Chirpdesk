#!/usr/bin/env python3
"""Validate plugin manifests and the published repository index against the shipped schemas.

Run from the repository root:

    python scripts/validate-schemas.py

CI installs `jsonschema` for this step; the app itself takes no dependency on it.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

try:
    import jsonschema
except ImportError:  # pragma: no cover - CI installs it
    print("jsonschema is required: python -m pip install jsonschema", file=sys.stderr)
    raise SystemExit(2)

ROOT = Path(__file__).resolve().parent.parent


def read(path: Path) -> object:
    with path.open(encoding="utf-8") as handle:
        return json.load(handle)


def validate(schema_path: Path, document_path: Path) -> bool:
    schema = read(schema_path)
    try:
        jsonschema.validate(read(document_path), schema)
    except jsonschema.ValidationError as error:
        print(f"INVALID {document_path.relative_to(ROOT)}: {error.message}")
        return False
    return True


def main() -> int:
    manifest_schema = ROOT / "docs" / "plugin.schema.json"
    index_schema = ROOT / "docs" / "repository-index.schema.json"

    documents = [
        (manifest_schema, path)
        for pattern in ("plugins/*/plugin.json", "registry-src/*/plugin.json")
        for path in sorted(ROOT.glob(pattern))
    ]
    documents.append((index_schema, ROOT / "registry" / "index.json"))

    failures = sum(0 if validate(schema, document) else 1 for schema, document in documents)
    checked = len(documents)

    if failures:
        print(f"{failures} of {checked} document(s) failed validation")
        return 1

    print(f"{checked} document(s) valid")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
