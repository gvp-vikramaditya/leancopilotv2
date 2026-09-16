# Repository instructions

This repository is the source of the Agent Plugins 1.0 plugin
`leancopilotv2`.

- Preserve `plugin.json` and the fixed Agent Plugins 1.0 component locations.
- Keep `.github/plugin/marketplace.json` metadata synchronized with
  `plugin.json`.
- Skills belong in immediate subdirectories of `skills/`.
- Copilot commands belong in `com.github.copilot/commands/`.
- Keep skill frontmatter valid and bodies concise.
- Use progressive disclosure: core procedures belong in `SKILL.md`; optional
  detail belongs in that skill's `references/` directory.
- Add deterministic helper programs to `scripts/` and tests to `tests/`.
- Prefer Python standard-library implementations unless a dependency is
  essential to the skill's purpose.
- Validate changes with `python scripts\validate_skill.py .` and
  `python -m unittest discover -s tests -v`.
