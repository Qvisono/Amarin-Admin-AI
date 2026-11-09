"""Проверки, что незавершённый или ухудшившийся опыт не объявляется успешным."""
import json
from pathlib import Path
import tempfile
import unittest

from analyze import analyze


class ScreeningTests(unittest.TestCase):
    def setUp(self):
        parent = Path(__file__).resolve().parents[2] / "release/prompt-eval-test-temp"
        parent.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=parent)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.write("manifest.json", {"case_ids": ["case"], "models": "model", "repetitions": 1})
        self.write("ledger.json", {"calls": [{"charged": "0.001", "status": "priced"}]})
        for side in ("baseline", "candidate"):
            self.write(side + ".json", {"model": "model", "side": side, "repetition": 0,
                "id": "case", "failures": [], "response": {"choices": [{"finish_reason": "stop"}],
                "usage": {}, "cost": {"usd": "0.001"}}})

    def write(self, name, value):
        (self.root / name).write_text(json.dumps(value), encoding="utf-8")

    def verdict(self, worse):
        self.write("judge__case.json", {"identity": ["model", 0, "case"], "a": "baseline",
            "b": "candidate", "verdict": {"worse": worse, "a_score": 10, "b_score": 9 if worse == "b" else 10}})

    def test_complete_equal_results_pass_the_screening_only(self):
        self.verdict("equal")
        self.assertTrue(analyze(self.root, self.root / "ledger.json")["screening_gate"]["passed"])

    def test_missing_judgment_is_not_counted_as_equal(self):
        gate = analyze(self.root, self.root / "ledger.json")["screening_gate"]
        self.assertFalse(gate["passed"])
        self.assertEqual(1, gate["missing_judgments"])

    def test_candidate_loss_cannot_be_hidden_by_overall_pass_counts(self):
        self.verdict("b")
        gate = analyze(self.root, self.root / "ledger.json")["screening_gate"]
        self.assertFalse(gate["passed"])
        self.assertEqual(1, gate["judge_loss_signals"])


if __name__ == "__main__":
    unittest.main()
