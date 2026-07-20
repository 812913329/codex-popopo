#!/usr/bin/env python3
"""Legacy compatibility wrapper for the canonical model robustness harness."""
from __future__ import annotations

import runpy
import sys
from pathlib import Path

CURRENT = Path(__file__).resolve()
TARGET = CURRENT.parents[2] / "model-prompt-injection-robustness" / "scripts" / "model_robustness_harness.py"

if not TARGET.exists():
    raise SystemExit(f"Canonical harness not found: {TARGET}")

sys.argv[0] = str(TARGET)
runpy.run_path(str(TARGET), run_name="__main__")
