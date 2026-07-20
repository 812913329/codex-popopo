# Legacy Alias Validation and Rollout Note

This directory is the compatibility alias for the canonical shared skill `model-prompt-injection-robustness`.

## Prefer these canonical assets
- skill: `model-prompt-injection-robustness`
- directory: `/root/.agents/skills/custom/red-teaming/model-prompt-injection-robustness`
- script: `scripts/model_robustness_harness.py`

## Legacy assets kept working
- alias skill: `claude-prompt-injection-robustness`
- alias directory: `/root/.agents/skills/custom/red-teaming/claude-prompt-injection-robustness`
- wrapper script: `scripts/claude_robustness_harness.py`

## Validation commands

### Canonical self-test
```bash
python3 /root/.agents/skills/custom/red-teaming/model-prompt-injection-robustness/scripts/model_robustness_harness.py --self-test
```

### Legacy wrapper self-test
```bash
python3 /root/.agents/skills/custom/red-teaming/claude-prompt-injection-robustness/scripts/claude_robustness_harness.py --self-test
```

### Optional current-model config check
```bash
HERMES_HOME=/root/.hermes/profiles/main-worker \
python3 /root/.agents/skills/custom/red-teaming/model-prompt-injection-robustness/scripts/model_robustness_harness.py \
  --use-current-model \
  --show-config
```

## Migration rule

When updating notes, automation, or examples, prefer the canonical generic name. Keep the alias only for compatibility with already-existing references.
