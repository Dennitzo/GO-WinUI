import unittest
import json
import tempfile
from unittest.mock import patch
from pathlib import Path
from test_catalog import catalog, gguf


class ReasoningLanguageTests(unittest.TestCase):
    def test_installed_deepseek_jinja_replays_the_entire_generated_prefix(self):
        # Optional local acceptance: no inference/model load. The mandatory
        # fixture test below covers adaptation without a local model install.
        try:
            import jinja2
        except ImportError:
            self.skipTest("Jinja2 is unavailable for the optional real-template render")
        root = Path.home() / ".cache" / "huggingface" / "hub"
        if not root.is_dir():
            self.skipTest("No local model catalog")
        installed = next((model for model in catalog.discover_models(root)
                          if model["id"].startswith("coding/DeepSeek")), None)
        if installed is None:
            self.skipTest("No local DeepSeek model")
        original = catalog.model_metadata(installed["path"], allow_metadata_only=True)["tokenizer.chat_template"]
        adapted = catalog.german_reasoning_template(original)
        if adapted is None or "set keep_reasoning = true" not in adapted:
            self.skipTest("Installed model uses another DeepSeek template")
        environment = jinja2.Environment()
        environment.filters["from_json"] = json.loads
        history = [dict(role="system", content="Antworte auf Deutsch."), dict(role="user", content="Merke EICHE-42.")]
        reasoning, answer = "Ich merke mir die Kennung.", "EICHE-42"
        following = [*history, dict(role="assistant", content=answer, reasoning_content=reasoning),
                     dict(role="user", content="Nenne die Kennung erneut.")]
        def render(template, messages):
            return environment.from_string(template).render(messages=messages, bos_token="<｜begin▁of▁sentence｜>",
                thinking=True, add_generation_prompt=True, tools=[])
        generated = render(original, history) + reasoning + "</think>" + answer + "<｜end▁of▁sentence｜>"
        self.assertFalse(render(original, following).startswith(generated))
        self.assertTrue(render(adapted, following).startswith(generated))
        self.assertEqual(render(original, history), render(adapted, history))
        # Stop may occur inside reasoning, including after whitespace. Rendering
        # the historical partial turn must not trim or rewrite any decoded byte.
        for partial in ("  Unfertiger Gedanke", "Unfertiger Gedanke \n\t", " \n"):
            with self.subTest(partial=repr(partial)):
                interrupted = [*history, dict(role="assistant", content="", reasoning_content=partial),
                    dict(role="user", content="Vorherige Runde unterbrochen; neuer Auftrag folgt."),
                    dict(role="user", content="Nenne die Kennung erneut.")]
                self.assertTrue(render(adapted, interrupted).startswith(render(adapted, history) + partial))
        partial_content = "Antwort beginnt \n\t"
        interrupted = [*history, dict(role="assistant", content=partial_content, reasoning_content=reasoning),
                       dict(role="user", content="Setze fort.")]
        self.assertTrue(render(adapted, interrupted).startswith(
            render(adapted, history) + reasoning + "</think>" + partial_content))

    def test_deepseek_keeps_historical_reasoning_without_requiring_tools(self):
        template = "{%- set dsml_token = 'DSML' -%}\n" \
            "{%- set keep_reasoning = tp.has or (loop.index0 > last_user_idx.value) -%}\n" \
            "{%- if keep_reasoning and thinking -%}{{- message['reasoning_content'] -}}{%- endif -%}"
        adapted = catalog.german_reasoning_template(template)
        self.assertIn("set keep_reasoning = true", adapted)
        # History replays by the stored reasoning of each turn, never by the
        # current thinking flag: a reasoning switch must keep the KV prefix.
        self.assertNotIn("if keep_reasoning and thinking", adapted)
        self.assertIn(catalog.DEEPSEEK_HISTORY_BY_CONTENT, adapted)
        self.assertIsNone(catalog.german_reasoning_template(adapted))
        self.assertEqual(template.replace("tp.has or (loop.index0 > last_user_idx.value)", "true")
                         .replace(catalog.DEEPSEEK_HISTORY_BY_FLAG, catalog.DEEPSEEK_HISTORY_BY_CONTENT), adapted)

    def test_installed_deepseek_history_prefix_is_identical_across_reasoning_switches(self):
        try:
            import jinja2
        except ImportError:
            self.skipTest("Jinja2 is unavailable for the optional real-template render")
        root = Path.home() / ".cache" / "huggingface" / "hub"
        if not root.is_dir():
            self.skipTest("No local model catalog")
        installed = next((model for model in catalog.discover_models(root)
                          if model["id"].startswith("coding/DeepSeek")), None)
        if installed is None:
            self.skipTest("No local DeepSeek model")
        original = catalog.model_metadata(installed["path"], allow_metadata_only=True)["tokenizer.chat_template"]
        adapted = catalog.german_reasoning_template(original)
        if adapted is None or catalog.DEEPSEEK_HISTORY_BY_CONTENT not in adapted:
            self.skipTest("Installed model uses another DeepSeek template")
        environment = jinja2.Environment()
        environment.filters["from_json"] = json.loads
        tools = [{"type": "function", "function": {"name": "coding_read", "description": "Lies", "parameters": {"type": "object"}}}]
        def render(template, messages, thinking):
            return environment.from_string(template).render(messages=messages, bos_token="<BOS>",
                thinking=thinking, add_generation_prompt=True, tools=tools)
        history = [dict(role="system", content="Antworte auf Deutsch."), dict(role="user", content="Merke EICHE-42.")]
        end = "<｜end▁of▁sentence｜>"
        # Turn generated with thinking off: exact prefix "</think>" + content.
        no_reasoning = [*history, dict(role="assistant", content="EICHE-42", reasoning_content=""),
                        dict(role="user", content="Nenne die Kennung erneut.")]
        generated_off = render(adapted, history, False) + "EICHE-42" + end
        self.assertTrue(render(adapted, no_reasoning, False).startswith(generated_off))
        self.assertTrue(render(adapted, no_reasoning, True).startswith(generated_off),
                        "switching reasoning on must not rewrite a turn generated without reasoning")
        self.assertFalse(render(original, no_reasoning, True).startswith(generated_off),
                         "the stock template rewrites history by the current flag")
        # Turn generated with thinking on: exact prefix "<think>" + reasoning + "</think>" + content.
        with_reasoning = [*history, dict(role="assistant", content="EICHE-42", reasoning_content="Ich merke sie mir."),
                          dict(role="user", content="Nenne die Kennung erneut.")]
        generated_on = render(adapted, history, True) + "Ich merke sie mir." + "</think>" + "EICHE-42" + end
        self.assertTrue(render(adapted, with_reasoning, True).startswith(generated_on))
        self.assertTrue(render(adapted, with_reasoning, False).startswith(generated_on),
                        "switching reasoning off must not drop the generated reasoning prefix")

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
