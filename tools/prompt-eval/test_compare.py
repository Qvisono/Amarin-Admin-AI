import json
import tempfile
import unittest
from pathlib import Path

import compare


def write(folder, name, value):
    (folder / name).write_text(json.dumps(value), encoding="utf-8")


class CompareTests(unittest.TestCase):
    def test_sign_test_is_symmetric_and_exact(self):
        self.assertEqual(compare.sign_test(0, 0), 1.0)
        self.assertAlmostEqual(compare.sign_test(3, 3), 1.0)
        self.assertAlmostEqual(compare.sign_test(0, 5), 2 / 32)
        self.assertAlmostEqual(compare.sign_test(5, 0), compare.sign_test(0, 5))

    def test_wilson_interval_contains_the_estimate(self):
        low, high = compare.wilson(5, 100)
        self.assertLess(low, 0.05)
        self.assertGreater(high, 0.05)
        self.assertEqual(compare.wilson(0, 0), (0.0, 1.0))

    def test_judge_outcome_follows_the_hidden_side_not_the_letter(self):
        with tempfile.TemporaryDirectory() as root:
            folder = Path(root)
            for side, failures in (("baseline", []), ("candidate", ["forbidden tool"])):
                write(folder, f"m__0__x__{side}.json",
                      {"model": "m", "repetition": 0, "id": "x", "side": side, "failures": failures})
            write(folder, "judge__m__0__x.json", {"identity": ["m", 0, "x"], "a": "baseline", "b": "candidate",
                  "verdict": {"worse": "b", "a_score": 9, "b_score": 4, "a_problems": [], "b_problems": ["p"],
                              "reason": "r"}})
            result = compare.summarize(folder)
            self.assertEqual(result["judge"], {"loss": 1})
            self.assertEqual(result["objective"], {"baseline_pass": 1, "candidate_pass": 0})
            self.assertEqual(len(result["objective_flips"]), 1)
            self.assertEqual(result["judge_losses"][0]["candidate_problems"], ["p"])


if __name__ == "__main__":
    unittest.main()
