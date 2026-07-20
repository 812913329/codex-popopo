#!/usr/bin/env python3
from __future__ import annotations

import contextlib
import importlib.util
import io
import sys
from pathlib import Path


def load_harness():
    harness_path = Path(__file__).resolve().parent / "model_robustness_harness.py"
    spec = importlib.util.spec_from_file_location("model_robustness_harness_dynamic", harness_path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Unable to load harness: {harness_path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def main() -> int:
    try:
        harness = load_harness()
        with contextlib.redirect_stdout(io.StringIO()):
            harness.self_test()
        print("激活成功")
        return 0
    except Exception as exc:
        print(f"激活失败: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
