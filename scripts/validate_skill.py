#!/usr/bin/env python3
"""Validate an Agent Plugins 1.0 manifest and its bundled skills."""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

NAME_PATTERN = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
PLUGIN_NAME_PATTERN = re.compile(r"^[a-z0-9]+(?:[.-][a-z0-9]+)*$")
MAX_NAME_LENGTH = 64
MAX_DESCRIPTION_LENGTH = 1024
PLUGIN_SCHEMA = "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json"


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


def validate_skill(skill_root: Path) -> list[str]:
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

    if name and skill_root.name != name:
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


def validate_plugin(plugin_root: Path) -> list[str]:
    errors: list[str] = []
    manifest_path = plugin_root / "plugin.json"

    if not manifest_path.is_file():
        return [f"Missing required plugin manifest: {manifest_path}"]

    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        return [f"Invalid plugin.json: {error}"]

    if manifest.get("$schema") != PLUGIN_SCHEMA:
        errors.append(f"plugin.json $schema must be {PLUGIN_SCHEMA}")

    plugin_name = manifest.get("name", "")
    if not plugin_name or not PLUGIN_NAME_PATTERN.fullmatch(plugin_name):
        errors.append(
            "Plugin name must contain lowercase letters, numbers, dots, and "
            "single hyphens only"
        )

    skills_root = plugin_root / "skills"
    if not skills_root.is_dir():
        errors.append(f"Missing skills directory: {skills_root}")
        return errors

    skill_directories = sorted(
        path for path in skills_root.iterdir() if path.is_dir()
    )
    if not skill_directories:
        errors.append("Plugin must contain at least one skill under skills/")

    for skill_directory in skill_directories:
        errors.extend(
            f"{skill_directory.relative_to(plugin_root)}: {error}"
            for error in validate_skill(skill_directory)
        )

    marketplace_path = plugin_root / ".github" / "plugin" / "marketplace.json"
    if marketplace_path.is_file():
        try:
            marketplace = json.loads(marketplace_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            errors.append(f"Invalid marketplace.json: {error}")
        else:
            matching_plugins = [
                plugin
                for plugin in marketplace.get("plugins", [])
                if plugin.get("name") == plugin_name
            ]
            if not matching_plugins:
                errors.append(
                    "marketplace.json must contain an entry matching the "
                    "plugin name"
                )
            else:
                marketplace_plugin = matching_plugins[0]
                if marketplace_plugin.get("version") != manifest.get("version"):
                    errors.append(
                        "marketplace plugin version must match plugin.json"
                    )

    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "plugin_root",
        nargs="?",
        default=".",
        type=Path,
        help="Path to the plugin directory (default: current directory)",
    )
    args = parser.parse_args()
    plugin_root = args.plugin_root.resolve()
    errors = validate_plugin(plugin_root)

    if errors:
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        return 1

    print(f"Valid Agent Plugin: {plugin_root}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
