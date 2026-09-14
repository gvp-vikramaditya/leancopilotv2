---
name: leancopilotv2
description: Provides repository-defined guidance, references, assets, and automation for Lean Copilot workflows. Use when a user explicitly asks to use leancopilotv2 or requests a workflow documented by this skill.
metadata:
  author: gvp-vikramaditya
  version: "0.1.0"
---

# Lean Copilot

Use this skill for tasks covered by this repository's procedures and resources.

## Workflow

1. Identify the user's requested outcome and constraints.
2. Read only the relevant files in `references/` when their descriptions match
   the task.
3. Reuse templates or static resources from `assets/` rather than recreating
   them.
4. Prefer deterministic helpers in `scripts/` for repeatable operations.
5. Validate generated or modified output before reporting completion.

## Current scope

This is an initial scaffold. Before publishing the skill, replace this section
with concrete domain procedures, inputs, outputs, edge cases, and validation
steps. Update the frontmatter description with the exact phrases that should
activate the skill.

## Resources

- Read `references/README.md` before adding or using detailed guidance.
- Read `assets/README.md` before adding or using templates and static files.
- Run `python scripts/validate_skill.py .` after changing the skill structure or
  `SKILL.md` metadata.
