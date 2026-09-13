import unittest
import tempfile
from pathlib import Path
from test_catalog import catalog, gguf


class ReasoningLanguageTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
    def test_instructions_are_localized_without_prefilled_reasoning(self):
        template = """{{ message.content }}
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
        self.assertEqual(template[template.index("{%- if add_generation_prompt %}"):],
                         localized[localized.index("{%- if add_generation_prompt %}"):])
        self.assertIn("<think>\\n\\n</think>\\n\\n", localized)
        self.assertTrue(localized.startswith("{{ message.content }}"))
        self.assertIsNone(catalog.german_reasoning_template("{{ messages }}"))
        self.assertIsNone(catalog.german_reasoning_template(localized))

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
