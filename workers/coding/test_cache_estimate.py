import unittest
from test_catalog import catalog


class CacheEstimateTests(unittest.TestCase):
    def test_deepseek4_mla_cache_fits_disk_allowance_at_its_full_context(self):
        metadata = {"general.architecture": "deepseek4", "deepseek4.block_count": 43,
                    "deepseek4.attention.head_count": 64, "deepseek4.attention.head_count_kv": 1,
                    "deepseek4.attention.key_length": 512, "deepseek4.attention.indexer.key_length": 128}
        estimate = catalog.estimate_q8_session_bytes(metadata, 1048576)
        self.assertIsNotNone(estimate)
        self.assertLess(estimate, 64 * 1024**3)
        # Measured local DeepSeek snapshots were ~35.98 KiB/token (138334
        # tokens: 4977004680 bytes); do not underreserve this observed state.
        self.assertGreater(catalog.estimate_q8_session_bytes(metadata, 138334), 4977004680)
        # Query head count is not a dense KV dimension in this MLA architecture.
        metadata["deepseek4.attention.head_count"] = 128
        self.assertEqual(estimate, catalog.estimate_q8_session_bytes(metadata, 1048576))

    def test_deepseek4_missing_indexer_dimension_requests_safe_fallback(self):
        metadata = {"general.architecture": "deepseek4", "deepseek4.block_count": 43,
                    "deepseek4.attention.key_length": 512}
        self.assertIsNone(catalog.estimate_q8_session_bytes(metadata, 1048576))

    def test_q8_estimate_uses_gqa_dimensions_not_model_parameter_count(self):
        metadata = {"general.architecture": "qwen3", "qwen3.block_count": 48,
                    "qwen3.embedding_length": 5120, "qwen3.attention.head_count": 40,
                    "qwen3.attention.head_count_kv": 8}
        estimate = catalog.estimate_q8_session_bytes(metadata, 220000)
        self.assertGreater(estimate, 220000 * 48 * 8 * 128 * 2)
        self.assertLess(estimate, 220000 * 256 * 1024)

    def test_unknown_and_incomplete_architectures_request_fallback(self):
        self.assertIsNone(catalog.estimate_q8_session_bytes({"general.architecture": "future"}, 220000))
        self.assertIsNone(catalog.estimate_q8_session_bytes({"general.architecture": "qwen3"}, 220000))

    def test_hybrid_recurrent_memory_is_added_without_removing_attention_layers(self):
        metadata = {"general.architecture": "qwen3next", "qwen3next.block_count": 48,
                    "qwen3next.embedding_length": 5120, "qwen3next.attention.head_count": 40,
                    "qwen3next.attention.head_count_kv": 8}
        base = catalog.estimate_q8_session_bytes(metadata, 220000)
        metadata.update({"qwen3next.ssm.state_size": 128, "qwen3next.ssm.inner_size": 4096,
                         "qwen3next.ssm.group_count": 8, "qwen3next.ssm.conv_kernel": 4})
        self.assertGreater(catalog.estimate_q8_session_bytes(metadata, 220000), base)


if __name__ == "__main__":
    unittest.main()
