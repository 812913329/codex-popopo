---
name: claude-prompt-injection-robustness
description: Use when old sessions, saved notes, or automation still reference the legacy Claude-named prompt-injection robustness skill and you need the backward-compatible alias that points to the canonical model-agnostic workflow.
version: 2.0.0
author: Hermes Agent
license: MIT
metadata:
  hermes:
    tags: [prompt-injection, jailbreak-evaluation, robustness, safety, system-prompt, red-teaming, legacy-alias, claude]
    related_skills: [model-prompt-injection-robustness, hermes-agent, verification-before-completion]
---

# Legacy Alias: Claude Prompt-Injection Robustness

> 新用法请优先加载 `model-prompt-injection-robustness`。这个目录保留只是为了不打断旧的 `skill_view(...)`、笔记和脚本引用。

## Overview

这是 canonical skill `model-prompt-injection-robustness` 的**兼容别名**。

注意：canonical skill 的默认语义已经升级为 **“激活红队模式”入口**，而不是一上来就进入 prompt-injection 测试演示。

现在它默认面向：
- **红队模式激活与授权边界声明**
- **Web / 主机 / AD / 云 / 容器 / API / AI 系统等红队任务的统一入口**
- 以及在用户**明确指定时**才进入的 OpenAI-compatible 模型鲁棒性/注入测试分支

如果你在写新的文档、自动化或操作说明，请继续优先引用新的 canonical skill 名；其中 `scripts/activate_redteam.py` 是“激活红队模式”的首选入口，`scripts/model_robustness_harness.py` 则是专题测试分支。

## Canonical Target

- Canonical skill: `model-prompt-injection-robustness`
- Canonical directory: `/root/.agents/skills/custom/red-teaming/model-prompt-injection-robustness`
- Canonical script: `scripts/model_robustness_harness.py`

## Compatibility Retained Here

- Legacy skill name: `claude-prompt-injection-robustness`
- Legacy directory: `/root/.agents/skills/custom/red-teaming/claude-prompt-injection-robustness`
- Legacy wrapper script: `scripts/claude_robustness_harness.py`
- Legacy alias note: `references/session-validation-and-rollout.md`

旧脚本现在只是一个 wrapper，会把所有 CLI 参数转发给 canonical harness，所以旧命令仍然可用。

## Quick Commands

### Preferred canonical self-test

```bash
python3 /root/.agents/skills/custom/red-teaming/model-prompt-injection-robustness/scripts/model_robustness_harness.py --self-test
```

### Legacy self-test still works

```bash
python3 /root/.agents/skills/custom/red-teaming/claude-prompt-injection-robustness/scripts/claude_robustness_harness.py --self-test
```

## When to Use

Use this alias when:
- an old session still references `claude-prompt-injection-robustness`
- a saved note or runbook still points at `claude_robustness_harness.py`
- you need backwards compatibility while migrating docs or automation

Do **not** use this alias when:
- creating new instructions
- creating new automation
- naming new shared skills

## Migration Note

Switch new references from:
- `claude-prompt-injection-robustness`
- `scripts/claude_robustness_harness.py`

To:
- `model-prompt-injection-robustness`
- `scripts/model_robustness_harness.py`

## Evidence Preservation Note

在红队/安全测试会话中，`tmp` 报告、截图、trace、浏览器 `localStorage` / `sessionStorage` 等证据现场，应默认保留到用户明确确认“测试可以结束/可以清理”为止。

## Verification Checklist

- [ ] `skill_view(name="claude-prompt-injection-robustness")` still loads
- [ ] `skill_view(name="model-prompt-injection-robustness")` loads as the canonical skill
- [ ] legacy wrapper self-test passes
- [ ] canonical harness self-test passes
