#!/usr/bin/env python3
"""Validate all homelab/v1 shapes in the working tree against the schema."""
import glob, json, sys, yaml
from jsonschema import Draft202012Validator

schema = json.load(open("Infrastructure/schema/shape.schema.json"))
Draft202012Validator.check_schema(schema)
v = Draft202012Validator(schema)

files = sorted({f for p in ("Infrastructure/**/*.yaml", "stacks/**/*.yaml") for f in glob.glob(p, recursive=True)})
checked = failed = 0
for f in files:
    try:
        doc = yaml.safe_load(open(f))
    except Exception as e:
        print(f"::error file={f}::YAML parse error: {e}"); failed += 1; continue
    if not isinstance(doc, dict) or doc.get("apiVersion") != "homelab/v1":
        continue  # not a shape
    checked += 1
    errs = sorted(v.iter_errors(doc), key=lambda e: list(e.path))
    if errs:
        failed += 1
        for e in errs:
            print(f"::error file={f}::/{'/'.join(map(str, e.path))}: {e.message}")
    else:
        print(f"  OK  {f}")
print(f"\n{checked} shape(s) checked, {failed} invalid.")
sys.exit(1 if failed else 0)
