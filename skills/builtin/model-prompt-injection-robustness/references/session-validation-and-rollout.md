# Session Validation and Rollout Notes

## Canonical skill after generic rename

This directory is now the canonical shared skill for safe, model-agnostic prompt-injection robustness evaluation.

Canonical assets:
- skill: `model-prompt-injection-robustness`
- directory: `/root/.agents/skills/custom/red-teaming/model-prompt-injection-robustness`
- script: `scripts/model_robustness_harness.py`

Legacy compatibility retained:
- alias skill: `claude-prompt-injection-robustness`
- alias directory: `/root/.agents/skills/custom/red-teaming/claude-prompt-injection-robustness`
- wrapper script: `/root/.agents/skills/custom/red-teaming/claude-prompt-injection-robustness/scripts/claude_robustness_harness.py`

## Recommended validation sequence

```bash
python3 /root/.agents/skills/custom/red-teaming/model-prompt-injection-robustness/scripts/model_robustness_harness.py --self-test

python3 /root/.agents/skills/custom/red-teaming/claude-prompt-injection-robustness/scripts/claude_robustness_harness.py --self-test

HERMES_HOME=/root/.hermes/profiles/main-worker \
python3 /root/.agents/skills/custom/red-teaming/model-prompt-injection-robustness/scripts/model_robustness_harness.py \
  --show-config --use-current-model
```

Expected results:
- canonical self-test passes
- legacy wrapper self-test passes
- current-model resolution prints model / base_url / config source without exposing the raw API key

## Migration rule

For new docs, automation, and operator notes, prefer:
- `model-prompt-injection-robustness`
- `scripts/model_robustness_harness.py`

Keep the Claude-named alias only so existing references continue to work during migration.
