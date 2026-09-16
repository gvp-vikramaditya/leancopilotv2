---
name: documentation-to-skills
description: Converts repository documentation into focused GitHub Copilot agent skills. Use when asked to skillify docs, extract skills from docs, migrate documentation into SKILL.md files, inventory documentation for reusable workflows, or create repository skills from a docs folder.
---

# Convert Repository Documentation to Agent Skills

Distill existing documentation into searchable, triggerable, task-oriented
skills. Do not merely copy documentation into `SKILL.md`.

## Inputs and defaults

- Use the documentation path supplied by the user.
- If no path is supplied, scan `docs/`.
- Use the output directory supplied by the user.
- If no output directory is supplied, create project skills in
  `.github/skills/`.
- Do not delete or modify the source documentation.

## 1. Inventory and classify

Scan all files under the documentation path. Classify each file using every
type that applies:

- **Conceptual / how it works**: architecture, data flow, design decisions, or
  system behavior.
- **Reference / sample queries**: reusable SQL, API calls, configuration, CLI
  commands, or code snippets.
- **Procedural / how-to**: steps for setup, deployment, operation, or
  troubleshooting.
- **Glossary / definitions**: terms, fields, or entities with little procedure.

Produce an inventory table with these columns:

```text
file path | type(s) | subsystem/topic | one-line summary
```

Group files by subsystem or topic. These groups are candidates for skill
boundaries.

## 2. Define skill boundaries

For each topic cluster, decide whether to create:

- **One skill** when the sources describe one coherent workflow users would ask
  about together.
- **Multiple skills** when the sources cover distinct triggering situations
  that should load independently.
- **No skill** when content is pure reference data with no plausible task
  trigger. Flag it as a candidate for a reference file or search index and
  explain why.

Propose a list containing each skill's name, one-line purpose, and source files.
When interactive user input is available, ask for approval before writing the
skills. When running non-interactively, proceed and record the boundary
reasoning in the migration notes.

## 3. Create each skill

Create this structure inside the output directory:

```text
<output-directory>/<skill-name>/
|-- SKILL.md
`-- references/
    |-- queries.md
    `-- <other-topic>.md
```

Only create reference files that the skill needs.

### Write `SKILL.md`

Use YAML frontmatter containing only `name` and `description`:

```markdown
---
name: <kebab-case-name>
description: >
  <what the skill helps with and the concrete situations that trigger it>.
---

# <Skill Title>

<One-line purpose.>

## When this applies

<Concrete trigger situations.>

## What to do

<Task-oriented workflow, mental model, code locations, and gotchas.>

## Reference material

Read `references/queries.md` when example queries are needed.
```

The description is the primary discovery mechanism. Include:

- The capability in plain language.
- Realistic phrases and situations that should trigger the skill.
- Enough trigger variants to avoid under-triggering.

Convert source material instead of transcribing it:

- Rewrite conceptual prose as checks, mental models, relevant code or system
  locations, common misconceptions, and decision-relevant facts.
- Rewrite procedural documentation as numbered imperative steps.
- Move sample queries, commands, and long code blocks into focused reference
  files.
- Keep `SKILL.md` under approximately 500 lines.
- For large domains, keep `SKILL.md` as the workflow and map to focused files
  under `references/`.

### Write reference files

- Preserve sample queries and commands close to verbatim.
- Carry forward applicable license or copyright notices from source files.
- Group artifacts by the operation or question they answer, not by source file.
- Add one line above each artifact explaining what it answers and what values
  must be adapted.
- Add a table of contents when a reference file exceeds approximately 300
  lines.

## 4. Cross-check against source documentation

For every generated skill:

1. Confirm that no factual claim contradicts its source documentation.
2. Confirm that every source query, command, and reusable example appears in an
   appropriate reference file.
3. Write three realistic user questions that should trigger the skill.
4. Check that the skill description plausibly matches all three questions.
5. Validate the skill's frontmatter, directory name, relative links, and file
   structure.

If a validation step fails, fix the generated content and repeat validation.

## 5. Produce migration notes

Create `SKILLS_MIGRATION_NOTES.md` at the repository root. Include:

- The documentation inventory.
- Boundary decisions and their reasoning.
- Each generated skill and its source documents.
- The three trigger-test questions for each skill.
- Content classified as reference-only rather than a skill.
- Any source ambiguities or claims that could not be verified.

The conversion is additive. Preserve all original documentation.
