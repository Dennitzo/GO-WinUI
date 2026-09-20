import unittest
from verify_stop_session_cache import select_reasoning, run_failure


class StopSessionCacheProbeTests(unittest.TestCase):
    def test_deepseek_boolean_reasoning_uses_advertised_default_in_both_roles(self):
        models = [dict(id="deepseek", role=role, reasoningEfforts=["none", "on"], defaultReasoningEffort="on")
                  for role in ("general", "coding")]
        for role in ("general", "coding"):
            self.assertEqual("on", select_reasoning(models, "deepseek", role))

    def test_reasoning_never_invents_low_for_a_model_without_effort_levels(self):
        self.assertIsNone(select_reasoning([dict(id="text", role="general")], "text", "general"))
        self.assertEqual("none", select_reasoning([dict(id="text", role="coding", reasoningEfforts=["none"])], "text", "coding"))

    def test_probe_failure_contains_actual_gateway_reason(self):
        reason = run_failure(dict(state="failed", errorCode="run.invalid_operation"),
                             [dict(type="run.failed", data=dict(message="Reasoning level is unsupported"))])
        self.assertIn("run.invalid_operation", reason)
        self.assertIn("Reasoning level is unsupported", reason)


if __name__ == "__main__":
    unittest.main()
