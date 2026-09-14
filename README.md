# leancopilotv2

A starter repository for a GitHub Copilot Agent Skill.

## Structure

```text
.
|-- SKILL.md                    # Skill metadata and core instructions
|-- assets/                     # Templates and static resources
|-- references/                 # Detailed, on-demand guidance
|-- scripts/                    # Deterministic helper scripts
|-- tests/                      # Tests for bundled scripts
`-- .github/
    |-- copilot-instructions.md # Instructions for contributors using Copilot
    `-- workflows/validate.yml  # Skill validation in CI
```

GitHub Copilot discovers project skills under `.agents/skills/<skill-name>/`.
To use this repository as a skill in another project, copy or clone this
repository to:

```text
<project>/.agents/skills/leancopilotv2/
```

The resulting path must contain `SKILL.md` directly:

```text
<project>/.agents/skills/leancopilotv2/SKILL.md
```

In VS Code, open Copilot Chat in Agent mode and run `/skills` to confirm that
`leancopilotv2` is discovered.

## Customize the skill

1. Update the `description` in `SKILL.md` with concrete tasks and trigger words.
2. Replace the starter workflow in `SKILL.md` with domain-specific procedures.
3. Put detailed documentation in `references/`.
4. Put reusable templates or static resources in `assets/`.
5. Put deterministic automation in `scripts/` and add tests in `tests/`.
6. Run validation:

   ```powershell
   python scripts\validate_skill.py .
   python -m unittest discover -s tests -v
   ```

The skill name must remain the same as its containing directory. If you rename
the repository directory, update the `name` field in `SKILL.md` to match.
To verify an installed copy's directory name as well as its metadata, run:

```powershell
python scripts\validate_skill.py --enforce-directory-name .
```

## Skill format

This repository follows the open [Agent Skills specification](https://agentskills.io/specification).
Keep `SKILL.md` concise and use relative links to load supporting resources only
when they are needed.