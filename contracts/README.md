# BricsCAD bridge contracts

`bricscad-dotnet-tools-v2.json` is the unchanged Barebone-Qt BricsCAD V26 tool
contract. Its legacy schema identifiers are intentionally retained so recorded
workflows and result envelopes remain compatible during the GO migration.

The transport is protocol 4 (`bridge-json-v4`): one UTF-8 JSON object per line,
with a maximum frame size of 8 MiB. Request IDs are integers and correlate
`request` and `response` messages. The plugin sends `hello` before processing
requests. The hello contains the rendezvous token, protocol number, contract
version and SHA-256 hash of the exact embedded tool contract.

GO listens only on a dynamically assigned loopback port. It atomically publishes
the active endpoint to `%LOCALAPPDATA%\GO\Bridge\active.json`. The file has the
schema `go.bricscad.bridge.rendezvous.v1` and contains `host`, `port`, `token`,
`protocol`, `bridgeBuild`, `contractVersion`, `contractHash`, `processId`,
`createdAtUtc` and optionally `appBuildId`. A plugin must reject a non-loopback
endpoint or mismatching protocol/contract identity.

On Windows, GO protects both the rendezvous directory and file with a protected
DACL that grants only the current user full access. Readers verify that ACL before
trusting the endpoint. The process id and creation time are checked together to
reject stale files after PID reuse.

The contract controls CAD capabilities only. AI/chat configuration and LM Studio
traffic never cross this plugin boundary.
