# Repository instructions

This repository is the source of an Agent Skill named `leancopilotv2`.

- Preserve the Agent Skills directory contract: `SKILL.md` belongs at the
  repository root and its `name` must match the containing directory.
- Keep the `SKILL.md` frontmatter valid and the body concise.
- Use progressive disclosure: core procedures belong in `SKILL.md`; optional
  detail belongs in `references/`; reusable files belong in `assets/`.
- Add deterministic helper programs to `scripts/` and tests to `tests/`.
- Prefer Python standard-library implementations unless a dependency is
  essential to the skill's purpose.
- Validate changes with `python scripts\validate_skill.py .` and
  `python -m unittest discover -s tests -v`.
