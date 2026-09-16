import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("coding_catalog", Path(__file__).with_name("catalog.py"))
catalog = importlib.util.module_from_spec(spec)
spec.loader.exec_module(catalog)


def gguf(path, architecture="qwen3", tensors=1, model_type="model", context=32768, pooling=None, template=None):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("wb") as stream:
        stream.write(struct.pack("<4sIQQ", b"GGUF", 3, tensors, 2 + int(context is not None) + int(pooling is not None) + int(template is not None)))
        for key, value in (("general.architecture", architecture), ("general.type", model_type)):
            key, value = key.encode(), value.encode()
            stream.write(struct.pack("<Q", len(key)) + key + struct.pack("<IQ", 8, len(value)) + value)
        for key, value in ((architecture + ".context_length", context), (architecture + ".pooling_type", pooling)):
            if value is not None:
                key = key.encode()
                stream.write(struct.pack("<Q", len(key)) + key + struct.pack("<II", 4, value))
        if template is not None:
            key, value = b"tokenizer.chat_template", template.encode()
            stream.write(struct.pack("<Q", len(key)) + key + struct.pack("<IQ", 8, len(value)) + value)
        stream.truncate(1024 * 1024 + 1)


class CatalogTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

    def test_only_complete_local_text_models_are_listed(self):
        gguf(self.root / "snapshots" / "qwen.gguf")
        gguf(self.root / "mmproj-F16.gguf", architecture="clip")
        gguf(self.root / "Qwen3-ASR-Q8.gguf")
        gguf(self.root / "vocab.gguf", tensors=0)
        gguf(self.root / "bge-m3.gguf", architecture="bert")
        gguf(self.root / "adapter.gguf", model_type="adapter")
        (self.root / "download.gguf").write_bytes(b"GGUF")
        (self.root / "model.safetensors").write_bytes(b"not gguf")
        models = catalog.discover(self.root)
        self.assertEqual(len(models), 1)
        self.assertTrue(models[0][0].startswith("coding/qwen~"))

    def test_split_model_requires_every_shard_and_yields_one_entry(self):
        gguf(self.root / "qwen-00001-of-00003.gguf", tensors=0)
        gguf(self.root / "qwen-00002-of-00003.gguf")
        self.assertEqual(catalog.discover(self.root), [])
        gguf(self.root / "qwen-00003-of-00003.gguf")
        self.assertEqual(len(catalog.discover(self.root)), 1)

    def test_metadata_only_first_shard_can_end_exactly_at_eof(self):
        first = self.root / "deepseek-00001-of-00003.gguf"
        gguf(first, architecture="deepseek4", tensors=0, context=1048576)
        with first.open("r+b") as stream:
            _, _, _, count = struct.unpack("<4sIQQ", stream.read(24))
            for _ in range(count):
                catalog.read_string(stream)
                catalog.skip_value(stream, struct.unpack("<I", stream.read(4))[0])
            stream.truncate(stream.tell())
        gguf(self.root / "deepseek-00002-of-00003.gguf")
        self.assertEqual(catalog.discover_models(self.root), [])
        gguf(self.root / "deepseek-00003-of-00003.gguf")
        model, = catalog.discover_models(self.root)
        self.assertEqual(model["context"], 1048576)
        self.assertEqual(model["path"], first)
        self.assertIsNone(catalog.model_metadata(first))
        # Incomplete metadata must not be accepted as a metadata-only shard.
        with first.open("r+b") as stream:
            stream.truncate(first.stat().st_size - 1)
        self.assertEqual(catalog.discover_models(self.root), [])

    def test_duplicate_filenames_have_distinct_stable_ids(self):
        gguf(self.root / "one" / "model.gguf")
        gguf(self.root / "two" / "model.gguf")
        first = catalog.discover(self.root)
        self.assertEqual(len({entry[0] for entry in first}), 2)
        self.assertEqual(first, catalog.discover(self.root))

    def test_router_disables_sleep_without_changing_single_model_memory_fitting(self):
        binary = self.root / "Unsloth with spaces" / "llama-server.exe"
        preset = self.root / "GO state" / "models.ini"
        command = catalog.runtime_command(binary, preset, "0.0.0.0", 8081, "2048", "auto")
        self.assertEqual(command[0], str(binary))
        self.assertEqual(command[command.index("--models-preset") + 1], str(preset))
        self.assertEqual(command[command.index("--sleep-idle-seconds") + 1], "-1")
        self.assertEqual(command.count("--sleep-idle-seconds"), 1)
        self.assertEqual(command[command.index("--models-max") + 1], "2")
        self.assertNotIn("--fit", command)  # CLI values override model-specific trial settings.
        self.assertNotIn("--n-gpu-layers", command)
        preset.parent.mkdir(parents=True, exist_ok=True)
        catalog.write_presets(self.root, preset)
        self.assertIn("fit = on\nfit-target = 2048", preset.read_text())
        self.assertIn("--no-models-autoload", command)

    def test_every_child_role_inherits_disabled_sleep_from_the_generated_preset(self):
        import configparser

        gguf(self.root / "Qwen.gguf")
        gguf(self.root / "mmproj-F16.gguf", architecture="clip")
        gguf(self.root / "bge-m3.gguf", architecture="bert", context=8192)
        target = self.root / "models.ini"
        models = catalog.write_presets(self.root, target)
        self.assertEqual({model["role"] for model in models}, {"general", "vision", "embedding"})
        presets = configparser.ConfigParser(default_section="*", interpolation=None)
        presets.read_string(target.read_text(encoding="utf-8").split("\n", 2)[2])
        self.assertEqual(set(presets.sections()), {model["id"] for model in models})
        for model in models:
            self.assertEqual(presets.getint(model["id"], "sleep-idle-seconds"), -1)

    def test_refresh_adds_and_removes_presets_without_touching_models(self):
        target = self.root / "models.ini"
        model = self.root / "qwen.gguf"
        catalog.write_presets(self.root, target)
        self.assertNotIn("[coding/", target.read_text())
        gguf(model)
        before = model.stat().st_mtime_ns
        catalog.write_presets(self.root, target)
        self.assertIn("[coding/", target.read_text())
        self.assertNotIn("hf-repo", target.read_text())
        self.assertEqual(before, model.stat().st_mtime_ns)
        model.unlink()
        catalog.write_presets(self.root, target)
        self.assertNotIn("[coding/", target.read_text())

    def test_embedding_uses_native_embedding_pooling_and_its_actual_context(self):
        gguf(self.root / "bge-m3-Q8.gguf", architecture="bert", context=8192, pooling=2)
        models = catalog.discover_models(self.root)
        self.assertEqual(len(models), 1)
        self.assertEqual(models[0]["role"], "embedding")
        self.assertEqual(models[0]["context"], 8192)
        self.assertEqual(models[0]["pooling"], "cls")
        target = self.root / "models.ini"
        catalog.write_presets(self.root, target)
        text = target.read_text()
        self.assertIn("embedding = true", text)
        self.assertIn("ctx-size = 8192", text)
        self.assertIn("pooling = cls", text)
        self.assertIn("ubatch-size = 8192", text)

    def test_vision_adds_projector_preset_without_changing_existing_coding_id(self):
        snapshot = self.root / "models--maker--qwen" / "snapshots" / ("1" * 40)
        model = snapshot / "Q4" / "Qwen-VL-Q4.gguf"
        gguf(model)
        original_id = catalog.discover(self.root)[0][0]
        gguf(snapshot / "mmproj-F16.gguf", architecture="clip")
        entries = catalog.discover_models(self.root)
        self.assertEqual([entry["role"] for entry in entries], ["general", "vision"])
        self.assertEqual(entries[0]["id"], original_id)
        target = self.root / "models.ini"
        catalog.write_presets(self.root, target)
        self.assertIn("mmproj = " + (snapshot / "mmproj-F16.gguf").as_posix(), target.read_text())

    def test_projectors_are_not_borrowed_from_another_snapshot_or_ambiguous_pair(self):
        snapshot = self.root / "models--maker--qwen" / "snapshots" / ("1" * 40)
        gguf(snapshot / "Qwen.gguf")
        gguf(snapshot.parent / ("2" * 40) / "mmproj-F16.gguf", architecture="clip")
        self.assertEqual(len(catalog.discover_models(self.root)), 1)
        gguf(snapshot / "mmproj-one.gguf", architecture="clip")
        gguf(snapshot / "mmproj-two.gguf", architecture="clip")
        self.assertEqual(len(catalog.discover_models(self.root)), 1)

    def test_training_maximum_is_published_without_disabling_runtime_memory_fitting(self):
        snapshot = self.root / "models--maker--qwen" / "snapshots" / ("1" * 40)
        gguf(snapshot / "Qwen.gguf", context=262144)
        gguf(snapshot / "mmproj-F16.gguf", architecture="clip")
        target = self.root / "models.ini"
        models = catalog.write_presets(self.root, target)
        self.assertEqual([model["context"] for model in models], [262144, 262144])
        self.assertTrue(all(model["contextPolicy"] == "max-fit" for model in models))
        text = target.read_text()
        self.assertEqual(text.count("go-context-train:262144,go-context-policy:max-fit"), 2)
        self.assertNotIn("ctx-size", text)  # Even an explicit zero disables native context fitting.
        self.assertEqual(text.count("predict = -1"), 2)

    def test_missing_or_invalid_context_is_not_replaced_by_an_invented_maximum(self):
        gguf(self.root / "unknown.gguf", context=None)
        gguf(self.root / "zero.gguf", context=0)
        gguf(self.root / "overflow.gguf", context=4294967295)
        self.assertEqual(catalog.discover_models(self.root), [])

    def test_embedding_context_follows_metadata_without_an_arbitrary_8192_cap(self):
        gguf(self.root / "embedding.gguf", architecture="nomic-bert", context=16384, pooling=1)
        target = self.root / "models.ini"
        models = catalog.write_presets(self.root, target)
        self.assertEqual(models[0]["context"], 16384)
        self.assertIn("ctx-size = 16384", target.read_text())

    def test_qwen_template_selects_its_declared_highest_effort_and_unlimited_thinking(self):
        template = "{% if enable_thinking %}{% if resolved_reasoning_effort not in ('xhigh', 'medium', 'low') %}{% endif %}{% endif %}"
        gguf(self.root / "Qwen3.8.gguf", architecture="qwen35", context=262144, template=template)
        target = self.root / "models.ini"
        models = catalog.write_presets(self.root, target)
        self.assertEqual(models[0]["reasoning"], {"enabled": True, "effort": "xhigh", "budget": -1,
                             "levels": ["none", "xhigh", "medium", "low"], "mode": "llama-native"})
        text = target.read_text()
        self.assertIn("reasoning = on\nreasoning-effort = xhigh", text)
        self.assertIn("reasoning-budget = -1", text)

    def test_future_model_exports_declared_levels_without_name_rules(self):
        gguf(self.root / "future.gguf", template="{% if reasoning_strength in ['low', 'high', 'custom_level'] %}{% endif %}")
        target = self.root / "models.ini"
        catalog.write_presets(self.root, target)
        text = target.read_text()
        self.assertIn("go-reasoning-levels:low|high|custom_level", text)
        self.assertIn("go-reasoning-default:high", text)

    def test_boolean_template_exposes_only_toggle_and_unknown_retains_auto(self):
        self.assertEqual(catalog.reasoning_profile({"tokenizer.chat_template": "{% if enable_thinking %}"})["levels"], ["none", "on"])
        self.assertEqual(catalog.reasoning_profile({"tokenizer.chat_template": "{{ messages }}"})["levels"], [])

    def test_gpt_oss_high_and_instruct_without_reasoning_are_distinct(self):
        gguf(self.root / "gpt.gguf", architecture="gpt-oss", context=131072, template="{{ reasoning_effort|default('medium') }}")
        gguf(self.root / "QwenVL-Instruct.gguf", architecture="qwen3vlmoe", context=262144, template="{{ messages }}")
        target = self.root / "models.ini"
        models = catalog.write_presets(self.root, target)
        by_name = {model["path"].name: model for model in models}
        self.assertEqual(by_name["gpt.gguf"]["reasoning"]["effort"], "high")
        self.assertIsNone(by_name["QwenVL-Instruct.gguf"]["reasoning"]["enabled"])
        self.assertIsNone(by_name["QwenVL-Instruct.gguf"]["reasoning"]["effort"])
        self.assertIn("reasoning = auto", target.read_text())

    def test_inherited_context_and_thinking_caps_cannot_override_highest_policy(self):
        source = {"PATH": "preserved", "LLAMA_ARG_CTX_SIZE": "4096", "LLAMA_ARG_THINK_BUDGET": "32",
                  "LLAMA_ARG_FIT_CTX": "262144", "LLAMA_ARG_REASONING_EFFORT": "low",
                  "LLAMA_ARG_CHAT_TEMPLATE_KWARGS": '{"enable_thinking":false}'}
        result = catalog.runtime_environment(source, self.root)
        self.assertEqual(result["PATH"], "preserved")
        self.assertEqual(result["HF_HUB_OFFLINE"], "1")
        self.assertFalse(any(key.startswith("LLAMA_ARG_") for key in result))
        self.assertEqual(source["LLAMA_ARG_CTX_SIZE"], "4096")


if __name__ == "__main__":
    unittest.main()
