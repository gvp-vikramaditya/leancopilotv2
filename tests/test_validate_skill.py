from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

from scripts.validate_skill import parse_frontmatter, validate_skill


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

            errors = validate_skill(root, enforce_directory_name=True)

            self.assertTrue(
                any("must match containing directory" in error for error in errors)
            )


if __name__ == "__main__":
    unittest.main()
