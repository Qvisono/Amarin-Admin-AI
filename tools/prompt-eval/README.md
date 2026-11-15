# Chat prompt evaluation

The suite compares two chat tech prompts using the actual assembled system prompts and tool schemas. All conversations are synthetic. Tool calls are inspected as data and never executed. The fixtures cover individual replies and decisions after simulated tool results; they do not run the real computer, browser, image generator, or vision pipeline.

History:

- First series (`REPORT-2026-11-08.md`): `candidates/compact.txt` (V1) and `candidates/tools-only.txt` (V2) were rejected.
- Second series (`REPORT-2026-11-08-v2.md`): `candidates/v6a.txt` was accepted and is now `ChatEngine.DefaultTechPrompt`; the previous default is `LegacyTechPrompts.V19`. `v3`, `v4`, `v5` and `v6c` are kept as evaluated steps and are not used by the app.

Export the prompts without network access. The baseline is always the current `ChatEngine.DefaultTechPrompt`:

```powershell
New-Item -ItemType Directory -Path release/prompt-eval -Force | Out-Null
$env:AMARIN_PROMPT_EXPORT = Join-Path (Get-Location) 'release/prompt-eval/export-new.json'
$env:AMARIN_PROMPT_CANDIDATE = Join-Path (Get-Location) 'tools/prompt-eval/candidates/<file>.txt'
dotnet test Amarin.AdminAI.Tests/Amarin.AdminAI.Tests.csproj -c Release --filter FullyQualifiedName~ChatPromptEvaluationExportTests
Remove-Item Env:AMARIN_PROMPT_EXPORT
Remove-Item Env:AMARIN_PROMPT_CANDIDATE
```

Run the local harness tests before making paid calls:

```powershell
python -m unittest discover -s tools/prompt-eval -p 'test_*.py'
```

The following commands spend Venice credit. Run them only with explicit authorization for this budget. They read `VENICE_API_KEY`, or decrypt a Venice key from the active app profile in memory. `--profile-context` additionally reads only that profile's main prompt and prepends it identically to both sides; that text is not saved in the results.

```powershell
python tools/prompt-eval/run.py run --profile-context --cases tools/prompt-eval/cases-v3.json --export release/prompt-eval/export-new.json --output release/prompt-eval/run-new
python tools/prompt-eval/run.py judge --cases tools/prompt-eval/cases-v3.json --export release/prompt-eval/export-new.json --output release/prompt-eval/run-new
python tools/prompt-eval/compare.py release/prompt-eval/run-new release/prompt-eval/run-aa
python tools/prompt-eval/analyze.py release/prompt-eval/run-new --require-no-loss
```

`cases.json` holds the original 49 scenarios; `make_cases_v3.py` writes `cases-v3.json` with 9 more (reply language, the line before a tool call, emoji, agent argument escaping), and `make_cases_lang.py` writes the language-only set `cases-lang.json`, which needs no judge.

Always run an A/A control: an export whose candidate equals the baseline (copy each variant's `baseline` into `candidate`). Identical prompts still disagree in objective checks and judge verdicts; `compare.py` reports both runs side by side with an exact two-sided sign test. A candidate is judged against that noise, not against zero.

Default models are the user's Venice chat slots observed on 2026-11-08: `claude-haiku-5-5` and `openai-gpt-61-terra`. Repetition zero uses medium reasoning for Haiku and low for Terra; odd repetitions use none. Input order alternates baseline/candidate, and the blind judge's A/B order is balanced and independent of variant names. The judge is a fallible additional check, not ground truth.

The ledger covers generation, judges, pilots and failures together. The cap is fixed at $4 equivalent; the last $0.05 remains unused. Before sending, each request reserves a conservative bound from current catalog prices, UTF-8 input bytes plus template allowance, and the explicit completion cap including reasoning. No retries or model fallbacks are made. A missing price keeps the full reservation and stops the run. `ledger.json` stopped that way on 2026-11-08; it is left unedited, and `ledger-2.json` continues the same budget with a first entry that carries the whole `ledger.json` total. Pass `--ledger` to use it. An exclusive process lock prevents simultaneous writers; a resumed run rejects changed prompts, cases, personal context, and request payloads.

Raw responses, catalog, hashes, server prices, token usage, and judge verdicts stay under the gitignored `release/prompt-eval/` directory. Summary tables must disclose failures, incomplete responses, invalid judgments, and any changed fixtures. A finite suite cannot establish zero quality loss, or a loss below 2% for all future inputs: with a few hundred pairs the confidence interval is several percentage points wide.

`analyze.py --require-no-loss` exits with code 1 when evaluations are missing, a candidate introduces additional failed checks, or a judge loss signal remains unresolved. Even identical prompts fail this gate, so use it only as a list of pairs to inspect. The factual counterexamples in `verify_findings.py` are checked with independent integer and rational arithmetic; generated code is never executed.
