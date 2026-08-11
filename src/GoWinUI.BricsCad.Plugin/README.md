# GO BricsCAD V26 plugin

Build the optional plugin with `windows\build-bricscad-plugin.ps1`. Load
`GOBricsCad.dll` from the generated artifact folder with BricsCAD's `NETLOAD`
command. Keep `GoWinUI.BricsCad.Protocol.dll` beside it; BricsCAD's own managed
API assemblies are intentionally not redistributed.

`GOPING` confirms that the managed module is loaded. The plugin then discovers
the running GO process through `%LOCALAPPDATA%\GO\Bridge\active.json` and connects
to its dynamic loopback port. It rejects rendezvous files whose current-user ACL,
token, protocol number or exact contract SHA-256 does not match.

The plugin contains no LM Studio or chat integration. It only implements the
ported BricsCAD V26 tool contract and CAD operations.
