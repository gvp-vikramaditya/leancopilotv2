from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

from scripts.validate_skill import (
    PLUGIN_SCHEMA,
    parse_frontmatter,
    validate_plugin,
    validate_skill,
)


class ParseFrontmatterTests(unittest.TestCase):
    def test_reads_required_fields(self) -> None:
        content = (
            "---\n"
            "name: example-skill\n"
            "description: Example description.\n"
            "---\n\n"
            "# Instructions\n"
        )

        metadata, errors = parse_frontmatter(content)

        self.assertEqual([], errors)
        self.assertEqual("example-skill", metadata["name"])
        self.assertEqual("Example description.", metadata["description"])


class ValidateSkillTests(unittest.TestCase):
    def test_accepts_valid_skill(self) -> None:
        with TemporaryDirectory() as directory:
            root = Path(directory) / "example-skill"
            root.mkdir()
            (root / "SKILL.md").write_text(
                "---\n"
                "name: example-skill\n"
                "description: Example description.\n"
                "---\n\n"
                "# Instructions\n",
                encoding="utf-8",
            )

            self.assertEqual([], validate_skill(root))

    def test_rejects_directory_name_mismatch(self) -> None:
        with TemporaryDirectory() as directory:
            root = Path(directory) / "wrong-name"
            root.mkdir()
            (root / "SKILL.md").write_text(
                "---\n"
                "name: example-skill\n"
                "description: Example description.\n"
                "---\n\n"
                "# Instructions\n",
                encoding="utf-8",
            )

            errors = validate_skill(root)

            self.assertTrue(
                any("must match containing directory" in error for error in errors)
            )


class ValidatePluginTests(unittest.TestCase):
    def test_accepts_plugin_with_valid_skill(self) -> None:
        with TemporaryDirectory() as directory:
            root = Path(directory)
            skill = root / "skills" / "example-skill"
            skill.mkdir(parents=True)
            (root / "plugin.json").write_text(
                "{"
                f"\"$schema\":\"{PLUGIN_SCHEMA}\","
                "\"name\":\"example-plugin\""
                "}",
                encoding="utf-8",
            )
            (skill / "SKILL.md").write_text(
                "---\n"
                "name: example-skill\n"
                "description: Example description.\n"
                "---\n\n"
                "# Instructions\n",
                encoding="utf-8",
            )

            self.assertEqual([], validate_plugin(root))

    def test_rejects_marketplace_version_mismatch(self) -> None:
        with TemporaryDirectory() as directory:
            root = Path(directory)
            skill = root / "skills" / "example-skill"
            marketplace = root / ".github" / "plugin"
            skill.mkdir(parents=True)
            marketplace.mkdir(parents=True)
            (root / "plugin.json").write_text(
                "{"
                f"\"$schema\":\"{PLUGIN_SCHEMA}\","
                "\"name\":\"example-plugin\","
                "\"version\":\"1.0.0\""
                "}",
                encoding="utf-8",
            )
            (marketplace / "marketplace.json").write_text(
                "{"
                "\"plugins\":[{"
                "\"name\":\"example-plugin\","
                "\"version\":\"2.0.0\","
                "\"source\":\".\""
                "}]"
                "}",
                encoding="utf-8",
            )
            (skill / "SKILL.md").write_text(
                "---\n"
                "name: example-skill\n"
                "description: Example description.\n"
                "---\n\n"
                "# Instructions\n",
                encoding="utf-8",
            )

            errors = validate_plugin(root)

            self.assertIn(
                "marketplace plugin version must match plugin.json",
                errors,
            )


if __name__ == "__main__":
    unittest.main()
