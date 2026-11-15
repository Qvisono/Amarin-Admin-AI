"""Независимая проверка двух фактических ошибок; код модели не исполняется."""
from fractions import Fraction
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def strong_test(n, base):
    odd = n - 1
    power = 0
    while odd % 2 == 0:
        odd //= 2
        power += 1
    value = pow(base, odd, n)
    if value in (1, n - 1):
        return True
    for _ in range(power - 1):
        value = pow(value, 2, n)
        if value == n - 1:
            return True
    return False


def finding(directory, case):
    path = ROOT / "release/prompt-eval" / directory / ("claude-haiku-5-5__0__" + case + "__candidate.json")
    if not path.exists():
        return {"raw_response_available": False}
    row = json.loads(path.read_text(encoding="utf-8"))
    content = row["response"]["choices"][0]["message"]["content"]
    return {"raw_response_available": True, "content_sha256": hashlib.sha256(content.encode()).hexdigest()}


def verify():
    left, right = 399165290221, 798330580441
    number = left * right
    bases = (2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37)
    assert 1 < left < number and 1 < right < number
    assert number == 318665857834031151167461
    assert number < 33 * 10**23
    assert all(number % base for base in bases)
    assert all(strong_test(number, base) for base in bases)

    exact = Fraction(9, 1000) / Fraction(585, 10000)
    assert exact == Fraction(2, 13)
    assert exact != Fraction(3, 19)
    return {
        "v1": {"composite": number, "factors": [left, right], "passes_all_twelve_bases": True,
               "inside_claimed_exact_range": True, **finding("run-2026-11-08", "code-python-boundary")},
        "v2": {"correct_fraction": str(exact), "incorrect_fraction": "3/19",
               "correct_decimal": float(exact), "incorrect_decimal": float(Fraction(3, 19)),
               **finding("run-v2-2026-11-08", "math-bayes")}
    }


if __name__ == "__main__":
    print(json.dumps(verify(), indent=2))
