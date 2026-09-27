# ExtensionHost security boundary

The Windows desktop supervisor starts each `ExtensionHost` in a new Windows Job Object before it sends the host handshake. The job does not allow breakaway and applies all of these limits to the trusted host, its extension entrypoint, and every permitted descendant:

- `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, so losing or disposing the supervisor handle terminates the complete process tree;
- finite per-process and aggregate job memory limits;
- a finite active-process limit;
- `JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION`;
- the existing wall-clock action timeout and bounded stdout/stderr protocol.

The host and one entrypoint consume the two available process slots when the manifest does not declare `process`. A process creation attempted by that entrypoint therefore fails at the operating-system boundary. A manifest with `process` receives a configurable but finite descendant allowance; those descendants remain in the same kill-on-close job.

This is process and resource containment, not a Windows AppContainer sandbox. `fileRead`, `fileWrite`, `workspaceRead`, `workspaceWrite`, and `network` are still checked at the invocation-contract boundary, but Windows does not prevent a signed entrypoint from using the current user's ambient file or network access. Robust AppContainer enforcement would require a broker that passes approved file handles, capability-specific network policy, an AppContainer profile lifecycle, and ACL provisioning for every executable/runtime dependency. Launching arbitrary native or .NET entrypoints in a partially configured AppContainer would either grant overly broad access or make legitimate runtimes fail unpredictably, so this host deliberately fails short of claiming that boundary.

Only packages signed by a configured trusted key should run until that broker exists. A future Linux Gateway sidecar must provide an equivalent cgroup/PID/resource boundary plus seccomp or another explicit syscall/network policy; the Windows Job Object implementation does not cover Linux containers.
