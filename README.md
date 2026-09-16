# leancopilotv2

An Agent Plugins 1.0 plugin that converts repository documentation into
focused, reusable GitHub Copilot agent skills.

The plugin provides:

- The `documentation-to-skills` skill for automatic or explicit activation.
- The `/extract-documentation-skills` slash command in GitHub Copilot CLI.
- A workflow for inventorying docs, defining skill boundaries, generating
  skills and references, validating them, and recording migration notes.

## Plugin structure

```text
.
|-- plugin.json
|-- skills/
|   `-- documentation-to-skills/
|       `-- SKILL.md
|-- com.github.copilot/
|   `-- commands/
|       `-- extract-documentation-skills.md
|-- scripts/
|-- tests/
`-- .github/
    |-- copilot-instructions.md
    `-- workflows/validate.yml
```

## Install for one repository

Repository scope is the recommended installation mode. It makes the plugin
available only while Copilot is working in the repository that declares it.

In the target repository, create or update
`.github/copilot/settings.json`:

```json
{
  "extraKnownMarketplaces": {
    "leancopilotv2": {
      "source": {
        "source": "github",
        "repo": "gvp-vikramaditya/leancopilotv2"
      }
    }
  },
  "enabledPlugins": {
    "leancopilotv2@leancopilotv2": true
  }
}
```

Commit that file to share the plugin with every contributor and with Copilot
cloud agent. Use `.github/copilot/settings.local.json` instead when the
installation should apply only to your local checkout; do not commit the local
settings file.

Start or restart Copilot CLI from the target repository. The plugin is
auto-installed and activated for that repository. Verify it with:

```text
/plugin list
/skills list
```

The `documentation-to-skills` skill should be listed.

### Use this local checkout from another repository

To develop the plugin locally while using it in another repository, create
`.github/copilot/settings.local.json` in the target repository:

```json
{
  "extraKnownMarketplaces": {
    "leancopilotv2": {
      "source": {
        "source": "directory",
        "path": "C:\\path\\to\\leancopilotv2"
      }
    }
  },
  "enabledPlugins": {
    "leancopilotv2@leancopilotv2": true
  }
}
```

Replace the path with the absolute path to this plugin repository. Keep
`settings.local.json` uncommitted because the path is specific to your machine.
The directory marketplace is loaded live, so plugin edits take effect in a new
Copilot session without reinstalling it.

### Use the plugin

Run the slash command:

```text
/extract-documentation-skills
```

Add overrides after the command when needed:

```text
/extract-documentation-skills scan architecture/ and write skills to .agents/skills/
```

You can also invoke the skill explicitly:

```text
Use the /documentation-to-skills skill to convert docs/ into repository skills.
```

## Install directly for the current user

To install from GitHub outside repository scope, register this repository as a
marketplace and install the plugin:

```powershell
copilot plugin marketplace add gvp-vikramaditya/leancopilotv2
copilot plugin install leancopilotv2@leancopilotv2
```

This imperative installation is user-scoped and may activate in other
repositories. Prefer the repository settings method above when the plugin
should be limited to one repository.

## Install only the skill

If plugin commands are not needed, install the skill at project scope with
GitHub CLI 2.90 or later:

```powershell
gh skill preview gvp-vikramaditya/leancopilotv2 documentation-to-skills
gh skill install gvp-vikramaditya/leancopilotv2 documentation-to-skills --scope project
```

Alternatively, copy `skills/documentation-to-skills/` into one of these
locations in the target repository:

```text
.github/skills/documentation-to-skills/
.agents/skills/documentation-to-skills/
.claude/skills/documentation-to-skills/
```

Then reload skills in Copilot CLI:

```text
/skills reload
```

## Local development

```powershell
python scripts\validate_skill.py .
python -m unittest discover -s tests -v
copilot plugin install .
```

Reinstall a locally installed plugin after changes because Copilot caches plugin
components.