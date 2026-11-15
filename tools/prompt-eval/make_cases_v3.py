"""Собирает cases-v3.json: прежние 49 сценариев без изменений и сценарии для правил, которые затрагивает V3."""
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
CYRILLIC = "[А-Яа-яЁё]"
BROKEN = "\\uFFFD"
EMOJI = "[\\U0001F300-\\U0001FAFF\\u2600-\\u27BF]"

extra = [
    {"id": "lang-english-explain", "variant": "browser",
     "messages": [{"role": "user", "content": "Why is the sky blue? Answer in two sentences, no pictures."}],
     "expect": {"no_tools": True, "content_none": [BROKEN, CYRILLIC]},
     "rubric": "Two accurate English sentences about Rayleigh scattering; no tools.", "judge": True},
    {"id": "lang-german", "variant": "browser",
     "messages": [{"role": "user", "content": "Kannst du mir kurz erklären, was ein Schaltjahr ist? Ohne Bilder bitte."}],
     "expect": {"no_tools": True, "content_all": ["\\b(ist|und|der|die|das|ein|Jahr)\\b"],
                "content_none": [BROKEN, CYRILLIC]},
     "rubric": "A short correct explanation in German: an extra day on February 29, the rule of 4/100/400.", "judge": True},
    {"id": "lang-switch-to-english", "variant": "browser",
     "messages": [
         {"role": "user", "content": "Привет! Как по-французски «спасибо»?"},
         {"role": "assistant", "content": "«Merci» :) А если хочется теплее, «Merci beaucoup»."},
         {"role": "user", "content": "And how do I politely say good night in Japanese? Keep it short."}],
     "expect": {"no_tools": True, "content_all": ["(?i)oyasumi"], "content_none": [BROKEN, CYRILLIC + "{3}"]},
     "rubric": "Answer in English, the language of the latest message: oyasumi nasai as the polite form.", "judge": True},
    {"id": "lang-switch-to-russian", "variant": "browser",
     "messages": [
         {"role": "user", "content": "What's the capital of Australia? One word."},
         {"role": "assistant", "content": "Canberra."},
         {"role": "user", "content": "А почему не Сидней? Объясни в двух предложениях."}],
     "expect": {"no_tools": True, "content_all": [CYRILLIC + "{4}"], "content_none": [BROKEN]},
     "rubric": "Answer in Russian: Canberra was chosen as a compromise between Sydney and Melbourne; accurate and brief.", "judge": True},
    {"id": "lang-requested-other", "variant": "browser",
     "messages": [{"role": "user", "content": "Напиши по-английски короткое поздравление коллеге с днём рождения, одно предложение."}],
     "expect": {"no_tools": True, "content_all": ["(?i)birthday"], "content_none": [BROKEN]},
     "rubric": "Provide a one-sentence English birthday greeting for a colleague, as requested.", "judge": True},
    {"id": "thinking-line-search", "variant": "browser",
     "messages": [{"role": "user", "content": "Какая последняя стабильная версия Python сейчас?"}],
     "expect": {"tools_any": ["search_web"], "content_all": ["\\S"], "content_none": [BROKEN]},
     "rubric": "Search the web for the current version and write one short line in Russian before the tool call saying what it is about to check; no recap, no list.", "judge": True},
    {"id": "thinking-line-agent", "variant": "browser",
     "messages": [{"role": "user", "content": "Посмотри, сколько свободного места осталось на диске C."}],
     "expect": {"tools_any": ["init_agent"], "content_all": ["\\S"], "content_none": [BROKEN]},
     "rubric": "Delegate the disk check to the agent with a complete prompt and write one short Russian line before the call; no manual instructions.", "judge": True},
    {"id": "emoji-joke", "variant": "browser",
     "messages": [{"role": "user", "content": "Расскажи короткий анекдот про программиста."}],
     "expect": {"no_tools": True, "content_none": [BROKEN, EMOJI]},
     "rubric": "One short, actually funny Russian joke about a programmer; at most one ASCII emoticon and no Unicode emoji.", "judge": True},
    {"id": "agent-json-escape", "variant": "browser",
     "messages": [{"role": "user", "content": "Поменяй в реестре значение \"Start Page\" в разделе HKCU\\Software\\Microsoft\\Internet Explorer\\Main на about:blank."}],
     "expect": {"tools_any": ["init_agent"], "args_contains": {"init_agent": ["Start Page", "Internet Explorer", "about:blank"]},
                "content_none": [BROKEN]},
     "rubric": "Delegate the registry change with the exact value name, key path and new value in valid JSON; no refusal or manual steps.", "judge": True},
]

cases = json.loads((HERE / "cases.json").read_text(encoding="utf-8-sig"))
ids = {c["id"] for c in cases}
assert not ids.intersection(c["id"] for c in extra)
(HERE / "cases-v3.json").write_text(json.dumps(cases + extra, ensure_ascii=False, indent=2), encoding="utf-8")
print(len(cases + extra))
