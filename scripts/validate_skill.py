#!/usr/bin/env python3
"""Validate the required structure and frontmatter of an Agent Skill."""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

NAME_PATTERN = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
MAX_NAME_LENGTH = 64
MAX_DESCRIPTION_LENGTH = 1024


def parse_frontmatter(content: str) -> tuple[dict[str, str], list[str]]:
    errors: list[str] = []
    lines = content.splitlines()

    if not lines or lines[0].strip() != "---":
        return {}, ["SKILL.md must start with YAML frontmatter delimited by ---"]

    try:
        closing_index = next(
            index for index, line in enumerate(lines[1:], start=1)
            if line.strip() == "---"
        )
    except StopIteration:
        return {}, ["SKILL.md frontmatter is missing its closing --- delimiter"]

    metadata: dict[str, str] = {}
    for line in lines[1:closing_index]:
        if not line or line[0].isspace() or line.lstrip().startswith("#"):
            continue
        if ":" not in line:
            errors.append(f"Invalid top-level frontmatter line: {line}")
            continue
        key, value = line.split(":", 1)
        metadata[key.strip()] = value.strip().strip("\"'")

    if not any(line.strip() for line in lines[closing_index + 1:]):
        errors.append("SKILL.md must contain instructions after the frontmatter")

    return metadata, errors


def validate_skill(
    skill_root: Path,
    *,
    enforce_directory_name: bool = False,
) -> list[str]:
    errors: list[str] = []
    skill_file = skill_root / "SKILL.md"

    if not skill_file.is_file():
        return [f"Missing required file: {skill_file}"]

    metadata, parse_errors = parse_frontmatter(
        skill_file.read_text(encoding="utf-8")
    )
    errors.extend(parse_errors)

    name = metadata.get("name", "")
    description = metadata.get("description", "")

    if not name:
        errors.append("Frontmatter must contain a non-empty name")
    elif len(name) > MAX_NAME_LENGTH:
        errors.append(f"Skill name must not exceed {MAX_NAME_LENGTH} characters")
    elif not NAME_PATTERN.fullmatch(name):
        errors.append(
            "Skill name must contain lowercase letters, numbers, and single "
            "hyphens only"
        )

    if enforce_directory_name and name and skill_root.name != name:
        errors.append(
            f"Skill name '{name}' must match containing directory "
            f"'{skill_root.name}'"
        )

    if not description:
        errors.append("Frontmatter must contain a non-empty description")
    elif len(description) > MAX_DESCRIPTION_LENGTH:
        errors.append(
            f"Description must not exceed {MAX_DESCRIPTION_LENGTH} characters"
        )

    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "skill_root",
        nargs="?",
        default=".",
        type=Path,
        help="Path to the skill directory (default: current directory)",
    )
    parser.add_argument(
        "--enforce-directory-name",
        action="store_true",
        help="Require the skill directory name to match the frontmatter name",
    )
    args = parser.parse_args()
    skill_root = args.skill_root.resolve()
    errors = validate_skill(
        skill_root,
        enforce_directory_name=args.enforce_directory_name,
    )

    if errors:
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        return 1

    print(f"Valid Agent Skill: {skill_root}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
