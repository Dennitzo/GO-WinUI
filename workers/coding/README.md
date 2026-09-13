# Native Windows model runtime

GO runs `llama-server.exe` directly on Windows for General, Coding, vision and
embedding requests, reading existing Unsloth model files in place.
The gateway and other GO services continue to run in Docker; the gateway connects
to Windows through `http://host.docker.internal:8081`.

GPU placement is negotiated through the supervisor on port 8082 (router port
plus one), advertised with the `go-gpu-policy:single-preferred-v1` catalog tag.
Before loading an unloaded model, GO reads current physical GPU free memory.
Weights (all GGUF shards and any projector) plus the configured VRAM reserve
must fit before attempting one GPU. Qwen3.8-27B prefers physical GPU1; other
models choose the eligible GPU with most free memory. CUDA device indices are
mapped using PCI bus order, independently of physical NVIDIA indices.

The single-GPU trial uses the full training context, all GPU layers, one device
and no automatic CPU offloading. llama itself validates the KV/cache/compute
allocation. A confirmed allocation failure allows exactly one retry using the
existing multi-GPU fitting policy after the failed child has exited. Other
errors are not disguised as VRAM failures. No running model's placement is
changed by catalog refresh. Decisions and failures are recorded in
`%USERPROFILE%/.go-winui/native-runtime/gpu-placement.jsonl`.

GO starts the shared native runtime when connecting to its local gateway or
refreshing the local model catalog. The portable package includes the launcher
and scanner in `Assets/NativeRuntime`, so this does not require a repository
checkout. `windows/start-ai-stack.ps1` also starts the same runtime. Its optional
`-NativeModelRoot` (also accepted as `-CodingModelRoot`) selects another existing model directory. The inspected
installation uses `C:/Users/AMD/.cache/huggingface/hub`. The native supervisor
uses Unsloth's existing executable at
`%USERPROFILE%/.unsloth/llama.cpp/build/bin/Release/llama-server.exe`. No model
files or runtime binaries are downloaded or copied.

Reasoning language is independent of the selected effort. GO adds a German
language instruction at the model request boundary. For recognized Jinja
templates with a final `<think>` generation prefix, the native catalog writes
an external template copy under `native-runtime/templates`: known Qwen effort
instructions are localized without changing their effort semantics. No text is
prefilled into the thinking channel; language announcements are discouraged. The
thinking-disabled branch, tool formatting, code and original GGUF remain
unchanged. Unrecognized templates retain their original form and the system
language instruction; no unknown template syntax is rewritten.

```powershell
.\windows\manage-coding-llama.ps1 -Action Start
.\windows\manage-coding-llama.ps1 -Action Status
.\windows\manage-coding-llama.ps1 -Action Stop
# Another existing model directory or binary:
.\windows\manage-coding-llama.ps1 -Action Start -ModelRoot D:\Models -BinaryPath D:\Llama\llama-server.exe
```

Start is idempotent and rejects a port occupied by an unrelated process. Stop
checks the saved supervisor PID, process start time, and command line. A Windows
Job Object binds only this supervisor's server and router children to its
lifetime. Unsloth's own processes and the separate `manage-llama-server.ps1`
installation are not stopped or reconfigured. Processes are launched hidden.

The scanner refreshes `%USERPROFILE%/.go-winui/native-runtime/models.ini` every ten seconds.
This user-profile path remains the same when launching GO from a normal Windows
process or an MSIX application. A Windows restart ends the native processes;
opening GO starts them again. No scheduled task or Windows service is installed.
Refreshing the Coding dropdown calls `/v1/models/coding`; the gateway requests
the native router's `v1/models?reload=1`. Stable path-derived IDs distinguish
quantizations and snapshots. Unloaded models load when their role is requested. The
router keeps at most one model resident. Idle sleep is explicitly disabled
(`--sleep-idle-seconds -1`, also inherited by every child preset). The installed
Unsloth build crashed with `0xC0000005` in `llama.dll` during a sleep/wake reload
triggered by native input-token counting on 2026-09-12. The manager continues to
unload models on actual model/workload switches; an idle model otherwise retains
its allocation. Changes to this process argument take effect at the next managed
runtime restart.
General and Coding share text-model IDs. Dedicated vision presets attach the
matching projector from the same snapshot; dedicated embedding presets use the
GGUF pooling metadata. Model switches remain within this single native runtime.

Only GGUF models with valid metadata are included. Tokenizer-only GGUFs, ASR,
adapters, standalone projectors, and incomplete split sets are excluded.
Unsloth's metadata-only first shard is supported when all tensor shards
exist. Safetensors are not converted or offered as llama.cpp models. Discovery
validates metadata and shard presence; llama.cpp validates tensor contents when
loading. All model accesses are reads.

Text and vision presets start from the GGUF training context maximum; native
memory fitting may reduce the allocated context to fit available GPU memory.
The loaded model's `/props` reports the actual allocation. Embedding presets
use their GGUF context maximum. Supported thinking models use the highest effort
declared by their chat template and no fixed reasoning-token cap. The runtime uses one
slot, Jinja native tool calling, prompt caching and streamed prefill progress.
Automatic GPU fitting reserves
2,048 MiB per GPU for existing workers. `-FitTargetMiB` and `-GpuLayers` adjust
these settings when starting the native supervisor. Large models may require CPU
offloading; model availability does not imply they fit in the available RAM or
generate quickly. Model load errors and the five-minute load limit are explicit.

The Coding loop emits a heartbeat and applies a 20-minute model-turn limit.
Prefill counts, text, tool selection, execution, and failures are distinct events.
No fixed random seed is sent. Complete native tool payloads are validated before
execution. Logs and ownership metadata are in `%USERPROFILE%/.go-winui/native-runtime`.
Ownership checks use the saved process start time and exact script/state
arguments, allowing repository and portable launchers to reuse the same process.
The app launches PowerShell without redirected output pipes, because the
long-lived supervisor can inherit those pipe handles. It passes a fresh
`-ErrorFile` path for each start; a failed launch writes the original exception
message there and exits with code 1. Normal CLI output remains available.

Coding runs default to 96 model rounds and 96 tool calls. The gateway environment
variables `GO_AI_CODING_MAXIMUM_MODEL_ROUNDS` (2–120) and
`GO_AI_CODING_MAXIMUM_TOOL_CALLS` (1–120) configure those budgets. Independent
reads can share one model round, while tool execution remains sequential.
The last eight rounds warn the model to finish; the last round reserves a
tool-free summary of findings, changes, validation and unfinished work. Reaching
the budget records `agent.run_limit` with that summary instead of claiming the
task was completed. Already emitted tool calls are processed before checking the
next model-round budget. The run duration includes client-tool resume periods.
Every five seconds, a bounded deadline check queues overdue unanswered client
proposals or waiting runs for the same single processor to end with `run.timeout`.
Terminal runs ignore stale queue entries, so a repeated result cannot restart a
completed task or repeat its file changes.

Validation:

```powershell
python -m unittest discover -s workers/coding -p 'test_*.py' -v
powershell -NoProfile -File workers/coding/test_launcher.ps1
python workers/coding/catalog.py --model-root C:\Users\AMD\.cache\huggingface\hub --list-models
dotnet test tests/GoAi.Server.Tests/GoAi.Server.Tests.csproj --filter CodingModelRuntimeTests
```

The inspected native build is Unsloth `b10840-mix-d5c17a0`, commit `58670d128`,
Windows CUDA13 older-GPU bundle including SM75 support. Model architecture and
quantization support must be checked against the chosen installed binary.
See the [llama.cpp server protocol](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md).
