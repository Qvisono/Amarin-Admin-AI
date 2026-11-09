"""Локальные проверки лимита и происхождения ответов. Сетевых запросов нет."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from decimal import Decimal
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("prompt_eval_runner", Path(__file__).with_name("run.py"))
runner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)


class EvaluationTests(unittest.TestCase):
    def setUp(self):
        # В песочнице Windows системный Temp может запрещать запись в созданные подпапки.
        parent = runner.ROOT / "release" / "prompt-eval-test-temp"
        parent.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="amarin-prompt-eval-tests-", dir=parent)
        self.root = Path(self.temp.name)
        self.ledger = self.root / "ledger.json"

    def tearDown(self):
        self.temp.cleanup()

    def test_pending_reservations_prevent_concurrent_overspend(self):
        budget = runner.Budget(self.ledger)
        def reserve(i):
            try:
                budget.begin(str(i), Decimal("1.1"))
                return True
            except RuntimeError:
                return False
        with ThreadPoolExecutor(max_workers=4) as pool:
            admitted = list(pool.map(reserve, range(4)))
        self.assertEqual(3, sum(admitted))
        self.assertEqual(Decimal("3.3"), budget.committed())

    def test_known_cost_releases_unused_reservation(self):
        budget = runner.Budget(self.ledger)
        index = budget.begin("first", Decimal("3"))
        budget.finish(index, {"cost": {"usd": "0.1"}})
        budget.begin("second", Decimal("3.8"))
        self.assertEqual(Decimal("3.9"), budget.committed())

    def test_exhausted_budget_does_not_send_request(self):
        budget = runner.Budget(self.ledger)
        budget.begin("occupied", Decimal("3.9"))
        with patch.object(runner, "reserve_cost", return_value=Decimal("0.1")), \
                patch.object(runner.urllib.request, "urlopen") as send:
            with self.assertRaisesRegex(RuntimeError, "Budget exhausted"):
                runner.call({"model": "m"}, "new", "test-secret", {"m": {}}, budget)
            send.assert_not_called()

    def test_pending_and_unknown_cost_stay_blocked_after_restart(self):
        for finish in (False, True):
            with self.subTest(finish=finish):
                ledger = self.root / (str(finish) + ".json")
                budget = runner.Budget(ledger)
                index = budget.begin("first", Decimal("0.1"))
                if finish:
                    budget.finish(index, None, "TimeoutError")
                restored = runner.Budget(ledger)
                with self.assertRaisesRegex(RuntimeError, "Unresolved"):
                    restored.begin("retry", Decimal("0.1"))
                self.assertEqual(Decimal("0.1"), restored.committed())

    def test_underestimated_cost_stays_blocked_after_restart(self):
        budget = runner.Budget(self.ledger)
        index = budget.begin("first", Decimal("0.1"))
        budget.finish(index, {"cost": {"usd": "0.2"}})
        with self.assertRaisesRegex(RuntimeError, "exceeded reservation"):
            runner.Budget(self.ledger).begin("second", Decimal("0.1"))

    def test_ledger_lock_excludes_another_process_and_releases(self):
        source = Path(runner.__file__).as_posix()
        script = (
            "import importlib.util, sys\n"
            f"s=importlib.util.spec_from_file_location('runner', {source!r})\n"
            "m=importlib.util.module_from_spec(s); s.loader.exec_module(m)\n"
            "with m.LedgerLock(sys.argv[1]):\n"
            "    print('acquired')\n"
        )
        with runner.LedgerLock(self.ledger):
            result = subprocess.run([sys.executable, "-c", script, str(self.ledger)], capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("already in use", result.stderr)
        result = subprocess.run([sys.executable, "-c", script, str(self.ledger)], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)

    def test_manifest_allows_only_increased_repetitions(self):
        path = self.root / "results" / "manifest.json"
        original = {"export_sha256": "original", "cases_sha256": "cases", "repetitions": 1}
        runner.preserve_manifest(path, original)
        expanded = {**original, "repetitions": 3}
        runner.preserve_manifest(path, expanded)
        self.assertEqual(runner.comparison_hash(original), runner.comparison_hash(expanded))
        for changed in ({**expanded, "export_sha256": "new"}, {**expanded, "repetitions": 2}):
            with self.assertRaises(RuntimeError):
                runner.preserve_manifest(path, changed)
        self.assertEqual(expanded, runner.read_json(path))

    def test_result_request_or_case_change_cannot_be_reused(self):
        case = {"id": "example", "messages": [{"role": "user", "content": "Hello"}]}
        manifest = {"export_sha256": "export", "repetitions": 1}
        row = {"id": "example", "side": "candidate", "model": "model", "repetition": 0,
               "case_sha256": runner.sha(case), "export_sha256": "export", "request_sha256": "request",
               "comparison_sha256": runner.comparison_hash(manifest)}
        runner.validate_result(row, case, "model", 0, "candidate", manifest, "request")
        with self.assertRaises(RuntimeError):
            runner.validate_result(row, case, "model", 0, "candidate", manifest, "new-request")
        with self.assertRaises(RuntimeError):
            runner.validate_result(row, {**case, "rubric": "changed"}, "model", 0, "candidate", manifest, "request")

    def test_payload_inserts_model_before_date_for_crlf_and_lf_exports(self):
        case = {"messages": [{"role": "user", "content": "Hello"}]}
        date = "Local date: 2026-11-08, Sunday (UTC+07:00)."
        for newline in ("\r\n", "\n"):
            separator = newline * 2
            variant = {
                "baseline": "Original rules" + newline + "Keep this line" + separator + date,
                "candidate": "Short rules" + newline + "Keep this line" + separator + date,
                "main": "Shared user instructions"
            }
            for side in ("baseline", "candidate"):
                with self.subTest(newline=repr(newline), side=side):
                    payload = runner.payload_for(case, variant, side, "test-model", [], 0)
                    head = variant[side][:-len(separator + date)]
                    expected = (variant["main"] + "\n\n" + head + separator
                                + "MODELS\nyou -> test-model" + separator + date)
                    self.assertEqual(expected, payload["messages"][0]["content"])
                    self.assertEqual(case["messages"], payload["messages"][1:])
                    self.assertEqual(1, payload["messages"][0]["content"].count(date))

    @staticmethod
    def judge_result(verdict, finish="stop"):
        return {"response": {"choices": [{"finish_reason": finish,
                "message": {"content": json.dumps(verdict)}}]}}

    def test_judge_rejects_incomplete_and_invalid_verdicts(self):
        good = {"a_score": 8, "b_score": 8, "a_problems": [], "b_problems": [],
                "worse": "equal", "reason": "Equivalent quality"}
        self.assertEqual(good, runner.judge_verdict(self.judge_result(good)))
        invalid = [{}, {**good, "a_score": True}, {**good, "b_score": 11},
                   {**good, "a_problems": "none"}, {**good, "worse": "candidate"},
                   {**good, "reason": ""}, {**good, "extra": 1},
                   {**good, "a_score": 9, "worse": "a"}]
        for verdict in invalid:
            with self.subTest(verdict=verdict), self.assertRaises(ValueError):
                runner.judge_verdict(self.judge_result(verdict))
        with self.assertRaises(ValueError):
            runner.judge_verdict(self.judge_result(good, finish="length"))

    def test_judge_detects_changed_export_before_any_call(self):
        cases = [{"id": "example", "messages": [], "rubric": "quality"}]
        cases_path = self.root / "cases.json"
        runner.write_json(cases_path, cases)
        output = self.root / "results"
        runner.write_json(output / "manifest.json", {"schema_version": 2, "export_sha256": "old",
                                                     "cases_sha256": runner.sha(cases)})
        args = argparse.Namespace(cases=str(cases_path), output=str(output))
        with patch.object(runner, "call") as send, self.assertRaisesRegex(RuntimeError, "manifest"):
            runner.judge(args, {"variants": []}, {}, None, "test-secret")
        send.assert_not_called()


if __name__ == "__main__":
    unittest.main()
