# leancopilotv2

An Agent Plugins 1.0 plugin that converts repository documentation into
focused, reusable GitHub Copilot agent skills.

The plugin provides:

- The `documentation-to-skills` skill for automatic or explicit activation.
- The `/extract-documentation-skills` slash command in GitHub Copilot CLI.
- A workflow for inventorying docs, defining skill boundaries, generating
  skills and references, validating them, and recording migration notes.

## LeanCLI prompt mode

Run a single request from the directory you want Lean to research:

```powershell
$lean = 'C:\prj\leancopilotv2\src\LeanCli\bin\Release\net10.0\lean.exe'
$resultFile = Join-Path $env:TEMP 'lean-result.json'
$progressFile = Join-Path $env:TEMP 'lean-progress.log'
Set-Location 'C:\prj\leancopilotv2\src\LeanCli'
& $lean --prompt "What does this project do?" --yes --model gpt-5-mini --local-model qwen2.5-coder:7b --json > $resultFile 2> $progressFile
$code = $LASTEXITCODE
$result = Get-Content -Raw $resultFile | ConvertFrom-Json
$result.output
if ($code -ne 0) { throw "Lean did not complete: $($result.status)" }
```

`--json` emits one object with `status`, `output` (the complete final text), and
`exitCode`. Without it, stdout contains the final text only. Progress goes to
stderr; no interactive prompts or terminal UI are used. Exit codes are **0**
for accepted completion, **1** for blocked/runtime failure, and **2** for invalid
arguments. Existing completion/evidence checks and the remote deadline still apply.

`--prompt -` reads a multiline prompt from redirected stdin through EOF:

```powershell
Get-Content -Raw .\prompt.txt | & $lean --prompt - --yes --json
```

`--yes` explicitly approves the remote handoff and file edits for this request.
Shells remain disabled unless `--execution` is supplied. `--sensitivity` defaults
to `Internal`. Model flags override the saved remote model and first available
local model without changing settings; specify both for repeatable runs.
Ollama must already be running with the selected model installed, and Copilot
must be authenticated. Prompt mode does not start a background Ollama server.
Use `lean --help` for all options; starting without arguments keeps the REPL.
Write test artifacts outside the research root so growing progress logs do not
invalidate the scope being assessed. Exit code 0 means the completion checks
accepted the result, not that its meaning is correct; automated tests should
also evaluate the returned explanation against their expected behavior.

## LeanCLI research evidence

The .NET terminal assistant in `src/LeanCli` delegates bounded repository
research to local Ollama and keeps planning, evaluation and final reasoning
with the remote head model. Codebase explanations default to a sampled overview
of every eligible file; exhaustive source/method review remains opt-in.

File reports deliver all distinct validated purposes and method findings, all
captured references, coverage and gaps, without the former per-report cuts.
Rejected-attempt metadata remains visible even after recovery or cache reuse;
rejected model output is not accepted as evidence. Budget/service stops include
available partial findings from unfinished files. This exposes collected
research data, **not every source line or hidden/generated/excluded file**.
Raw source remains local unless the remote explicitly requests a bounded excerpt.

The remote prompt requires skepticism: distinguish observed facts from model
reports and inferences, seek supporting and contradictory evidence for central
claims, and request focused proof before relying on doubtful interpretations.
Citations establish captured provenance, not semantic correctness. Runtime
claims require actual execution results. Proof requests remain bounded and
proportional to the task; unresolved claims must be qualified or reported as
blockers. These instructions do not guarantee model compliance.

Premature `task_complete` calls are checked before execution by a request-scoped
Copilot `preToolUse` hook, loaded with `--plugin-dir`. It requires assessed,
current research jobs, `status: done`, a nonempty `answer:`, and a cited,
successful source excerpt from the current snapshot for repository research.
Denial tells the head to continue the same conversation and obtain the missing
evidence. This is a workflow/provenance check, **not proof of semantic accuracy**.
The same checks run when the subprocess exits: rejected answer prose is withheld,
the terminal reports `blocked`, and the draft is saved as `rejected-answer.json`
in the request audit. Existing time and retry budgets still apply; blocked work
cannot be turned into a successful answer by rewording its status.

Hook files are generated only under the request's audit directory; no repository
or user-wide hooks are changed. Hook execution uses the Lean executable directly.
Copilot command-hook timeouts can fail open, so the final host check remains
mandatory. Invalid/guessed file paths are rejected before registering jobs,
allowing corrected requests without leaving phantom failed worklists behind.

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