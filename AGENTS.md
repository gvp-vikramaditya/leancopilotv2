# Contributor instructions

- Treat the repository root as the skill root.
- Keep `SKILL.md` focused on instructions needed whenever the skill activates.
- Put optional detail in `references/` and link to it from `SKILL.md` with a
  clear condition for when it should be read.
- Put reusable templates and static resources in `assets/`.
- Put deterministic, self-contained helpers in `scripts/` and cover them in
  `tests/`.
- Do not add dependencies unless the skill requires them.
- Run `python scripts\validate_skill.py .` and
  `python -m unittest discover -s tests -v` after structural changes.
