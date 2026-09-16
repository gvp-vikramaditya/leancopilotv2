---
description: Convert repository documentation into focused GitHub Copilot agent skills
argument-hint: Optional documentation path and output directory
---

Use the `documentation-to-skills` skill to convert this repository's
documentation into reusable agent skills.

Treat any text supplied after this command as overrides for the documentation
path, output directory, scope, or other constraints. Otherwise, scan `docs/`
and write project skills to `.github/skills/`.

Complete the inventory, boundary selection, skill generation, source
cross-check, trigger self-tests, and `SKILLS_MIGRATION_NOTES.md` output required
by the skill. Never delete or rewrite the original documentation.
