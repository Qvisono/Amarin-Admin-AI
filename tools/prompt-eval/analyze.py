"""Сводка результатов без маскировки неудачных пар и потерянной цены."""
import argparse
from collections import Counter, defaultdict
from decimal import Decimal
import json
from pathlib import Path


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def analyze(output, ledger):
    rows = []
    judges = []
    for path in sorted(output.glob("*.json")):
        row = read(path)
        if "side" in row:
            rows.append(row)
        elif path.name.startswith("judge__"):
            judges.append(row)
    pairs = defaultdict(dict)
    groups = defaultdict(list)
    for row in rows:
        pairs[(row["model"], row["repetition"], row["id"])][row["side"]] = row
        groups[(row["model"], row["side"])].append(row)
    summary = {"responses": len(rows), "unique_cases": len({r["id"] for r in rows}),
               "complete_pairs": sum(set(p) == {"baseline", "candidate"} for p in pairs.values()),
               "unpaired": [list(k) for k, p in pairs.items() if len(p) != 2],
               "objective": [], "judges": [], "objective_disagreements": []}
    manifest = read(output / "manifest.json")
    summary["expected_pairs"] = len(manifest["case_ids"]) * len(manifest["models"].split(",")) * manifest["repetitions"]
    for (model, side), results in sorted(groups.items()):
        choices = [(r.get("response") or {}).get("choices", [{}])[0] for r in results]
        usages = [(r.get("response") or {}).get("usage", {}) for r in results]
        costs = [(r.get("response") or {}).get("cost", {}) for r in results]
        summary["objective"].append({"model": model, "side": side, "n": len(results),
            "passed": sum(not r["failures"] for r in results),
            "failures": [{"id": r["id"], "repetition": r["repetition"], "reasons": r["failures"]}
                         for r in results if r["failures"]],
            "finish_reasons": dict(Counter(c.get("finish_reason", "missing") for c in choices)),
            "prompt_tokens": sum(u.get("prompt_tokens", 0) for u in usages),
            "completion_tokens": sum(u.get("completion_tokens", 0) for u in usages),
            "reported_usd_equivalent": str(sum((Decimal(str(c.get("usd", 0))) + Decimal(str(c.get("diem", 0)))
                                               for c in costs), Decimal(0)))})
    for identity, pair in sorted(pairs.items()):
        if len(pair) == 2 and bool(pair["baseline"]["failures"]) != bool(pair["candidate"]["failures"]):
            summary["objective_disagreements"].append({"identity": list(identity),
                "baseline": pair["baseline"]["failures"], "candidate": pair["candidate"]["failures"]})
    by_model = defaultdict(list)
    for row in judges:
        by_model[row["identity"][0]].append(row)
    for model, results in sorted(by_model.items()):
        count = Counter()
        losses = []
        scores = defaultdict(list)
        for row in results:
            verdict = row.get("verdict", {})
            if row.get("judge_error") or not verdict:
                count["invalid"] += 1
                continue
            worse = verdict.get("worse")
            outcome = "tie" if worse == "equal" else "loss" if row.get(worse) == "candidate" else "win"
            count[outcome] += 1
            for side in ("a", "b"):
                scores[row[side]].append(verdict[side + "_score"])
            if outcome == "loss":
                losses.append({"identity": row["identity"], "reason": verdict.get("reason"),
                               "candidate_problems": verdict.get(("a" if row["a"] == "candidate" else "b") + "_problems")})
        summary["judges"].append({"model": model, "outcomes": dict(count), "losses": losses,
            "mean_scores": {side: sum(values) / len(values) for side, values in scores.items()}})
    state = read(ledger)
    summary["budget"] = {"calls": len(state["calls"]),
        "committed_usd_equivalent": str(sum((Decimal(c["charged"]) for c in state["calls"]), Decimal(0))),
        "statuses": dict(Counter(c["status"] for c in state["calls"]))}
    additional_failures = [{"identity": list(identity),
        "new_failures": sorted(set(pair["candidate"]["failures"]) - set(pair["baseline"]["failures"]))}
        for identity, pair in pairs.items() if len(pair) == 2
        and set(pair["candidate"]["failures"]) - set(pair["baseline"]["failures"])]
    summary["screening_gate"] = {
        "additional_candidate_failures": additional_failures,
        "judge_loss_signals": sum(r["outcomes"].get("loss", 0) for r in summary["judges"]),
        "invalid_judgments": sum(r["outcomes"].get("invalid", 0) for r in summary["judges"]),
        "missing_judgments": summary["expected_pairs"] - len(judges)
    }
    gate = summary["screening_gate"]
    gate["passed"] = (summary["complete_pairs"] == summary["expected_pairs"] and not summary["unpaired"]
        and not additional_failures and not gate["judge_loss_signals"] and not gate["invalid_judgments"]
        and not gate["missing_judgments"] and all(c["status"] == "priced" for c in state["calls"]))
    return summary


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("--ledger", type=Path, default=Path(__file__).resolve().parents[2] / "release/prompt-eval/ledger.json")
    parser.add_argument("--require-no-loss", action="store_true", help="Fail on missing evaluations or any unresolved loss signal; not a statistical guarantee")
    args = parser.parse_args()
    result = analyze(args.output, args.ledger)
    (args.output / "summary.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2))
    if args.require_no_loss and not result["screening_gate"]["passed"]:
        raise SystemExit(1)
