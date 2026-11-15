"""Сравнивает опыт кандидата с контролем A/A, где обе стороны получают один и тот же промпт.

Контроль показывает, сколько расхождений и «проигрышей» у судьи даёт сама случайность генерации.
Кандидат оценивается на фоне этого шума, а не против нуля: одинаковые промпты тоже расходятся.
"""
import argparse
from collections import Counter, defaultdict
import json
import math
from pathlib import Path


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def load(output):
    pairs = defaultdict(dict)
    judges = {}
    for path in sorted(output.glob("*.json")):
        row = read(path)
        if "side" in row:
            pairs[(row["model"], row["repetition"], row["id"])][row["side"]] = row
        elif path.name.startswith("judge__"):
            judges[tuple(row["identity"])] = row
    return pairs, judges


def sign_test(wins, losses):
    """Двусторонний точный биномиальный тест: при равенстве промптов победы и поражения равновероятны."""
    n = wins + losses
    if n == 0:
        return 1.0
    k = min(wins, losses)
    tail = sum(math.comb(n, i) for i in range(k + 1)) / 2 ** n
    return min(1.0, 2 * tail)


def wilson(k, n, z=1.96):
    if n == 0:
        return (0.0, 1.0)
    p = k / n
    centre = (p + z * z / (2 * n)) / (1 + z * z / n)
    half = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / (1 + z * z / n)
    return (max(0.0, centre - half), min(1.0, centre + half))


def summarize(output):
    pairs, judges = load(output)
    complete = {k: p for k, p in pairs.items() if set(p) == {"baseline", "candidate"}}
    objective = Counter()
    flips = []
    per_model = defaultdict(Counter)
    for identity, pair in sorted(complete.items()):
        base = not pair["baseline"]["failures"]
        cand = not pair["candidate"]["failures"]
        objective["baseline_pass"] += base
        objective["candidate_pass"] += cand
        per_model[identity[0]]["baseline_pass"] += base
        per_model[identity[0]]["candidate_pass"] += cand
        per_model[identity[0]]["n"] += 1
        if base != cand:
            flips.append({"identity": list(identity), "baseline": pair["baseline"]["failures"],
                          "candidate": pair["candidate"]["failures"]})
    outcomes = Counter()
    losses = []
    model_outcomes = defaultdict(Counter)
    scores = defaultdict(list)
    for identity, row in sorted(judges.items()):
        verdict = row.get("verdict")
        if not verdict:
            outcomes["invalid"] += 1
            continue
        worse = verdict["worse"]
        outcome = "tie" if worse == "equal" else "loss" if row[worse] == "candidate" else "win"
        outcomes[outcome] += 1
        model_outcomes[identity[0]][outcome] += 1
        for side in ("a", "b"):
            scores[row[side]].append(verdict[side + "_score"])
        if outcome == "loss":
            side = "a" if row["a"] == "candidate" else "b"
            losses.append({"identity": list(identity), "reason": verdict["reason"],
                           "candidate_problems": verdict[side + "_problems"]})
    n = sum(outcomes[o] for o in ("win", "tie", "loss"))
    return {
        "pairs": len(complete), "incomplete": len(pairs) - len(complete),
        "objective": dict(objective), "objective_by_model": {m: dict(c) for m, c in per_model.items()},
        "objective_flips": flips,
        "judge": dict(outcomes), "judge_by_model": {m: dict(c) for m, c in model_outcomes.items()},
        "judge_missing": len(complete) - len(judges),
        "sign_test_p": sign_test(outcomes["win"], outcomes["loss"]),
        "loss_rate": outcomes["loss"] / n if n else None,
        "loss_rate_95ci": wilson(outcomes["loss"], n),
        "net_loss_rate": (outcomes["loss"] - outcomes["win"]) / n if n else None,
        "mean_scores": {side: sum(v) / len(v) for side, v in scores.items()},
        "judge_losses": losses,
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("candidate", type=Path)
    parser.add_argument("control", type=Path)
    args = parser.parse_args()
    result = {"candidate_vs_baseline": summarize(args.candidate), "baseline_vs_baseline": summarize(args.control)}
    (args.candidate / "compare.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2))
