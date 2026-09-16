# Fusion 1.14.2 Fork Repair

This fork remains based on upstream BONELAB Fusion 1.14.2. It preserves Fusion's public extension APIs and network packet formats while repairing desktop Steam ownership, Login/Logout lifetime, Browse completion, and direct mod.io token-file loading.

## Steam and Login behavior

BONELAB's process-owned Steam client is never shut down or reinitialized as SteamVR App ID `250820`. On desktop, Fusion uses the out-of-process Fusion Helper whenever BONELAB already owns Steam. Fusion can launch an installed Helper from the `BONELAB/Fusion Helper` folder, pins the child process to SteamVR App ID `250820` instead of inheriting a conflicting game/emulator Steam ID, waits at most 20 seconds for discovery and Steam initialization, rejects overlapping login attempts, and invalidates callbacks from older attempts. Logout can cancel an in-progress login and is safe to repeat.

Install the official [Fusion Helper v1.2.0](https://github.com/Lakatrazz/Fusion-Helper/releases/tag/v1.2.0) when the desktop SteamVR layer is used. The Helper keeps the SteamVR context outside BONELAB's process; no second Steam runtime is embedded in `LabFusion.dll`.

## Browse and networking

Steam and proxy Browse operations have a 15-second visible timeout and exactly one live completion. Logout, layer replacement, and generation changes invalidate late callbacks. A Steam layer permits only one outstanding native query at a time, retaining an uncancellable Facepunch task until native completion instead of destroying Steam state beneath it. Proxy lobby metadata requests run concurrently under one overall deadline, retain valid results when other metadata is malformed or late, and validate externally supplied counts and lengths.

The proxy receive path rejects stale-generation peers, validates packet payloads before use, recycles readers in a `finally` block, and rate-limits malformed-message diagnostics. High-frequency proxy ping logging was removed.

## mod.io token

The preferred token source is:

`BONELAB/UserData/FusionModIOToken.txt`

Fusion trims whitespace, rejects an empty token, caches the result, shares one load among concurrent callers, completes callbacks on the Melon/Unity coroutine path, and never logs the token. When the file is missing or invalid, Fusion falls back to its normal BONELAB mod.io settings. `FusionTokenBridge.dll` is not required and is not loaded or embedded.

Peer mod-info lookups use a 15-second bounded window with reliable resends and remove expired callbacks. Once a mod/file ID is known, mod.io metadata and content requests retry transient connection/server failures up to three times. Failure diagnostics include only the public mod/file IDs and HTTP status—not the access token—and missing content lengths no longer abort an otherwise valid download.

## Installation

1. Exit BONELAB.
2. Back up the current `BONELAB/Mods/LabFusion.dll`.
3. Remove any old `FusionTokenBridge.dll`.
4. Copy the released `LabFusion.dll` into `BONELAB/Mods`.
5. Keep BoneLib and Fusion's normal dependencies installed.
6. If using the token-file method, create `BONELAB/UserData/FusionModIOToken.txt` containing only the token.
7. Install the official Fusion Helper in `BONELAB/Fusion Helper` when the safe desktop SteamVR proxy route is required.
8. Launch BONELAB and press **Log In**.
9. Open Browse several times, join a server, disconnect and reconnect, change scenes, and Browse again.
10. Confirm Windows Event Viewer shows no new `steam_api64.dll` access-violation crash.

## Rollback

Exit BONELAB, remove the new DLL, restore the backed-up `LabFusion.dll`, and restore the previous Helper/network-layer configuration if it was changed.

## Validation boundary

The source builds against a real BONELAB/MelonLoader IL2CPP reference set, and the 33-scenario lifecycle/token simulation harness passes. Native Steam behavior still requires the real-game validation procedure above; a managed build and simulation cannot prove that every native driver/runtime combination is crash-free.
