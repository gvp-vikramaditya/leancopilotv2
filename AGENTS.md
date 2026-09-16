# Contributor instructions

- This repository is an Agent Plugins 1.0 plugin.
- Keep `plugin.json` compatible with the Agent Plugins 1.0 schema.
- Keep `.github/plugin/marketplace.json` synchronized with the plugin name,
  version, description, and source.
- Put portable skills in immediate subdirectories of `skills/`.
- Put Copilot-specific slash commands in `com.github.copilot/commands/`.
- Keep each `SKILL.md` focused on instructions needed whenever that skill
  activates.
- Put optional detail in a skill's `references/` directory and link to it with a
  clear condition for when it should be read.
- Put deterministic, self-contained helpers in `scripts/` and cover them in
  `tests/`.
- Do not add dependencies unless the skill requires them.
- Run `python scripts\validate_skill.py .` and
  `python -m unittest discover -s tests -v` after structural changes.
