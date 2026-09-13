# Local coding efficiency benchmark

This opt-in console runs the real GO HTTP gateway and `RunProcessor` against the selected native llama model. Local proposals use the production `LocalCodingToolExecutor` and `CodingRunEvidenceStore`. It does not require WinUI, a GO profile, Docker deployment, model downloads, or a native runtime restart.

Run it only after the current user run has ended and the native inference lane is available. Its database, workspaces and journals live exclusively under the explicitly supplied new output directory. The gateway binds to loopback on an ephemeral port. Only its run processor and client deadline service start; worker/model warmup services do not start. The model remains resident when this harness exits.

The harness refuses startup while `GO.exe`, a processing native slot, or active/queued GPU work on the user's gateway (`127.0.0.1:8080`) is detected. It checks the GO process and user gateway every three seconds while running. New user activity or an unavailable guard suspends **only the benchmark** and retains its checkpoint; it never cancels another run. This is a conservative observation guard, not a cross-process transaction lock: an operator must also keep unrelated native clients such as Unsloth idle, and verify there is no detached user run waiting for client input before starting. The production gateway must remain reachable for the guard.

## Build and verify without a model request

Builds/tests must be serialized with the repository's other .NET jobs.

```powershell
dotnet build tools/GoAi.CodingBenchmarks/GoAi.CodingBenchmarks.csproj -c Release
dotnet tools/GoAi.CodingBenchmarks/bin/Release/net10.0/GoAi.CodingBenchmarks.dll --verify-fixtures --output C:\absolute\new-fixture-check-directory --python C:\absolute\python.exe
```

`--verify-fixtures` invokes independent Python oracles against both the deliberately broken source and the reference implementation for all 12 tasks. It then runs deterministic schedule/qualification checks using explicitly marked synthetic unit data, and isolated file-system checks recorded in `storage-unit-checks.json`. On Windows these hold a reader without delete sharing, verify atomic replacement after release, and verify cancellation cleanup. These checks never count as measured AI outcomes.

## Current fixed-policy functional runs

Keep the same model, interpreter, source build and native runtime configuration throughout a comparison. Do not run a second gateway inference workload concurrently. Pick the exact ID from the actual native catalog.

```powershell
$env:GO_AI_CODING_BENCHMARK_LIVE = '1'
dotnet tools/GoAi.CodingBenchmarks/bin/Release/net10.0/GoAi.CodingBenchmarks.dll --output C:\absolute\new-pilot-directory --python C:\absolute\python.exe --model 'coding/installed-model-id' --variants working-state --tasks 01-add --repetitions 1
```

The live runner accepts only `working-state`: persistent working state with maximum supported reasoning on every model turn. Omitting task and repetition filters runs 12 tasks × 3 repetitions = 36 actual AI runs. Reference and adaptive live variants are removed. Historical comparison structures remain solely to interpret previous measurements; they cannot activate product profiles.

The 12 September fixed-policy smoke run completed in 164.0 seconds with six `xhigh` model turns, a real implementation edit, actual test execution, five passing independent oracle cases and unchanged protected files. Working-state snapshots and plan tool updates were retained. This validates this fixture, not a general performance gain.

The 12 contracts include targeted source search, multiple-module fixes, boundary cases, a long diagnostic whose middle marker must be retrieved from the real output archive, and a documentation-driven URL encoding fix. Only declared source modules may change. Public tests and other fixture files are protected, and the oracle resides outside the model's workspace. Only the documented Python test invocation (and the long-output fixture's diagnostic invocation) is allowed. One coding agent executes each task.

The research task uses a **frozen search-index transport**, explicitly identified as a benchmark fixture, with a real HTTPS fetch of the Python 3.13 original documentation. It is **not a live SearXNG search measurement**. Search index variability is excluded; public-source availability is not. Every actual fetch and result is retained in `events.jsonl`. A failed source fetch fails the task's research evidence requirement.

## Timing, results and historical qualification

`results.json` and `qualification.json` flush after each run. Each `jobs/<id>` preserves the exact request, full SSE log, live command progress, durable tool receipts, resulting files, independent oracle result and measured model-turn counters. Missing native counters remain null. Agent duration includes gateway/model/tool work, including any model load; the independent oracle and final metadata read are outside that duration. There are no benchmark-specific output-token or server round/tool limits; the harness itself has a default 900-second per-run stop, configurable with `--run-timeout-seconds` (30–86400), plus a 512-client-tool fixture bound.

Per-job `timings.json` separately reports native/gateway queue, token counting, prefill, generation, first-token and token counters, server-tool start-to-complete time, client-proposal-to-HTTP-acknowledgment time (including transport/client waiting), and actual command elapsed time from its real executor receipt. Each aggregate includes the number of measured and missing observations; an entirely unavailable measurement stays null. Client tools dispatch serially in this harness, so any head-of-line waiting is included rather than presented as process execution time.

The retained historical comparison gate (not an activation mechanism) requires all 12 tasks and all 3 repetitions of the candidate and its baseline. Both groups must pass every independent oracle and evidence requirement, preserve protected files, and share the same model ID, fixture definition hash, application/interpreter build hashes and runtime properties. Native runtime properties include context/slot configuration, template/build information and model-file size/mtime metadata. These file metadata are **not full weight-content hashes**. Changing weights during a benchmark is unsupported.

The gate requires at least **15% improvement in global median agent duration**, with no quality regression. Per-task median speedups are reported separately. A reduced pilot cannot qualify. These bounded fixtures establish performance on this suite; they do not establish universal coding quality or a speedup on other projects.

The recorded 12 September 2026 pilot completed `01-add--r1--working-state` in 258.0 seconds: eight model turns, seven client calls, an actual guarded edit, two passing public tests and five passing independent oracle cases. Its following adaptive run stopped on a Windows checkpoint replacement error, so there is no complete adaptive comparison or performance qualification. The replacement fix described below subsequently passed all five isolated Windows storage checks; the renewed fixture pass accepted all 12 reference implementations and rejected all 12 broken implementations without model requests.

The parallel-agent variants and separate native-slot experiment have been removed at the user's request. Their historical artifacts remain unchanged for audit, but the current runner neither offers nor executes them. See the [implementation and evidence report](../../docs/CODING-EFFICIENCY-IMPLEMENTATION.md) for measured pilot results and validation boundaries. New comparisons require fresh output directories; results from different source builds do not qualify together.

## Interruption and resume

Ctrl+C preserves completed runs and pending checkpoints. Resume using exactly the same arguments and `--resume`; existing completed results are skipped. Source/model/fixture/configuration changes refuse resume. An exclusive output-directory lock prevents two harnesses from replaying the same job.

Tool results are durably saved before submission. If execution started but no result survived interruption, that proposal receives an explicit unknown-outcome receipt and is never blindly executed again. Such a run cannot qualify. Resumed run timings are also retained but excluded from qualification because a process interruption changes timing conditions. Use a fresh output directory for a clean qualification attempt. The harness does not delete workspaces or evidence automatically.

JSON checkpoints use an atomic sibling-file replacement. Temporary Windows sharing, lock, or access-denied errors during replacement retry for at most five seconds; cancellation remains responsive and the previous complete file stays intact. Failed saves report the full destination and temporary path, and clean up only their own temporary file. Harness JSON readers permit read/write/delete sharing. External monitoring should likewise open files with `FileShare.ReadWrite | FileShare.Delete` so that reading a checkpoint does not block its replacement.
