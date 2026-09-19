import unittest
import tempfile
from unittest.mock import patch
from pathlib import Path
from test_catalog import catalog, gguf


class ReasoningLanguageTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
    def test_instructions_are_localized_with_a_neutral_native_language_heading(self):
        template = """{{ message.content }}
{{- '<|im_start|>' + message.role + '\\n<think>\\n' + reasoning_content + '\\n</think>\\n\\n' + content }}
{%- set reasoning_instructions = '' %}
{%- if add_generation_prompt %}
{{- '<|im_start|>assistant\\n' }}
{%- if enable_thinking is defined and enable_thinking is false %}
{{- '<think>\\n\\n</think>\\n\\n' }}
{%- else %}
{{- '<think>\\n' }}
{%- endif %}
{%- endif %}"""
        localized = catalog.german_reasoning_template(template)
        self.assertIn("Analysiere auf Deutsch", localized)
        self.assertNotIn("Ich prüfe", localized)
        self.assertIn("<think>\\nÜberlegung auf Deutsch:\\n", localized)
        self.assertEqual(template[template.index("{%- if add_generation_prompt %}"):],
                         localized[localized.index("{%- if add_generation_prompt %}"):].replace("Überlegung auf Deutsch:\\n", ""))
        self.assertIn("<think>\\n\\n</think>\\n\\n", localized)
        self.assertTrue(localized.startswith("{{ message.content }}"))
        self.assertIsNone(catalog.german_reasoning_template("{{ messages }}"))
        self.assertIsNone(catalog.german_reasoning_template(localized))

    def test_native_heading_is_returned_in_reasoning_and_history_never_inserts_it_twice(self):
        template = "{{- '<|im_start|>' + message.role + '\\n<think>\\n' + reasoning_content + '\\n</think>\\n\\n' + content }}" \
            "{%- if add_generation_prompt %}{{- '<think>\\n' }}{%- endif %}"
        localized = catalog.german_reasoning_template(template)
        self.assertEqual(1, localized.count("Überlegung auf Deutsch:\\n"))
        self.assertIn("'\\n<think>\\n' + reasoning_content", localized)
        self.assertIsNone(catalog.german_reasoning_template(localized))

    def test_unknown_history_form_never_gets_a_prefix_that_cannot_be_replayed(self):
        template = "{%- set reasoning_instructions = '' %}{%- if add_generation_prompt %}{{- '<think>\\n' }}{%- endif %}"
        localized = catalog.german_reasoning_template(template)
        self.assertIn("Analysiere auf Deutsch", localized)
        self.assertNotIn("Überlegung auf Deutsch", localized)
        self.assertIn("{{- '<think>\\n' }}", localized)

    def test_effective_reasoning_template_change_invalidates_native_snapshot_identity(self):
        template = "{{- '<|im_start|>' + message.role + '\\n<think>\\n' + reasoning_content + '\\n</think>\\n\\n' + content }}" \
            "{%- if add_generation_prompt %}{{- '<think>\\n' }}{%- endif %}"
        gguf(self.root / "model.gguf", template=template)
        manager = catalog.GpuLoadManager(self.root, self.root / "models.ini", self.root, 8081)
        model = catalog.discover_models(self.root)[0]["id"]
        with patch.object(catalog, "german_reasoning_template", return_value=template):
            original = manager.sessions._identity(model, "persisted-session")
        localized = manager.sessions._identity(model, "persisted-session")
        self.assertNotEqual(original, localized)

    def test_template_override_is_external_and_does_not_change_model_bytes(self):
        model = self.root / "model.gguf"
        template = "{%- set reasoning_instructions = '' %}{%- if add_generation_prompt %}{{- '<think>\\n' }}{%- endif %}"
        gguf(model, template=template)
        original = model.read_bytes()
        target = self.root / "models.ini"
        catalog.write_presets(self.root, target)
        self.assertEqual(original, model.read_bytes())
        self.assertIn("chat-template-file =", target.read_text())
        self.assertEqual(len(list((self.root / "templates").glob("*.jinja"))), 1)
        catalog.write_presets(self.root, target)
        self.assertEqual(len(list((self.root / "templates").glob("*.jinja"))), 1)


if __name__ == '__main__':
    unittest.main()
