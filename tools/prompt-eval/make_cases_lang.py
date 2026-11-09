"""Собирает cases-lang.json: сценарии языка ответа из cases-v3.json и короткие реплики не по-русски.

Нужен для отдельной проверки правила языка без судьи: исход определяется алфавитом ответа.
"""
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
CYRILLIC = "[А-Яа-яЁё]"
BROKEN = "\\uFFFD"

base = {c["id"]: c for c in json.loads((HERE / "cases-v3.json").read_text(encoding="utf-8"))}
picked = [base[i] for i in ("english-smalltalk", "lang-english-explain", "lang-german",
                            "lang-switch-to-english", "lang-switch-to-russian", "lang-requested-other")]
extra = [
    {"id": "en-fun-fact", "variant": "browser",
     "messages": [{"role": "user", "content": "Tell me one fun fact about octopuses. One sentence, no pictures."}],
     "expect": {"no_tools": True, "content_none": [BROKEN, CYRILLIC]},
     "rubric": "One accurate English sentence.", "judge": False},
    {"id": "en-cheer-up", "variant": "browser",
     "messages": [{"role": "user", "content": "Good morning! I'm a bit nervous about a job interview today. Cheer me up in a sentence or two."}],
     "expect": {"no_tools": True, "content_none": [BROKEN, CYRILLIC]},
     "rubric": "A short warm English reply.", "judge": False},
    {"id": "en-after-russian-report", "variant": "browser",
     "messages": [
         {"role": "user", "content": "Check how much free space is left on drive C, please."},
         {"role": "assistant", "content": None, "tool_calls": [{"id": "agent_1", "type": "function", "function": {
             "name": "init_agent", "arguments": "{\"prompt\":\"Check how much free space is left on drive C. Change nothing.\"}"}}]},
         {"role": "tool", "tool_call_id": "agent_1",
          "content": "Отчёт агента disk-c1: на диске C свободно 112 ГБ из 476 ГБ. Изменения не выполнялись."}],
     "expect": {"no_tools": True, "content_all": ["112"], "content_none": [BROKEN, CYRILLIC + "{3}"]},
     "rubric": "Retell the Russian agent report in English: 112 GB free of 476 GB, nothing changed.", "judge": False},
    {"id": "es-question", "variant": "browser",
     "messages": [{"role": "user", "content": "¿Cuál es la diferencia entre «ser» y «estar»? Responde brevemente."}],
     "expect": {"no_tools": True, "content_all": ["\\b(es|y|el|la|para|se)\\b"], "content_none": [BROKEN, CYRILLIC]},
     "rubric": "A brief correct answer in Spanish.", "judge": False},
]
cases = picked + extra
(HERE / "cases-lang.json").write_text(json.dumps(cases, ensure_ascii=False, indent=2), encoding="utf-8")
print(len(cases))
