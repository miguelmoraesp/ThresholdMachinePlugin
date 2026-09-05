# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Dalamud plugin for FFXIV ("Threshold Machine"). During a raid pull it polls the FFLogs GraphQL API, compares each player's live rDPS against per-job thresholds configured for kill-time brackets (e.g. "≤5:30"), and announces KEEP/WIPE verdicts in echo (and optionally party) chat.

## Build

```powershell
dotnet build -c Release
```

- Requires XIVLauncher with Dalamud dev hooks installed — `Dalamud.NET.Sdk` resolves its references from `%APPDATA%\XIVLauncher\addon\Hooks\dev\` (printed during restore).
- Targets `net10.0-windows7.0` on Dalamud API 15 (`Dalamud.NET.Sdk/15.0.0`); platform is x64. Output: `ThresholdMachine/bin/x64/Release/ThresholdMachine.dll`.
- No tests, no lint step. `.editorconfig` governs style (var-preferred, 4-space, LF, camelCase private fields, `On` prefix for event handlers).

## Release process

Bump `AssemblyVersion` in three places together: `ThresholdMachine/ThresholdMachine.csproj` (`<Version>`), `ThresholdMachine/ThresholdMachine.json`, and `repo.json` (which also carries the GitHub release download URL for the Dalamud third-party repo manifest).

## Architecture

Everything lives in `ThresholdMachine/` with one namespace per folder. Flow: **combat event → poller loop → FFLogs GraphQL → verdict → chat**.

### Data model (`Configuration.cs`)

`Configuration` → `FightList` → `Fight` → `KillTimeBracket` (keyed by an `m:ss` string) → per-`JobThreshold` + `DowntimePeriod`s. Downtime windows are subtracted from combat time before computing rDPS. Jobs are identified by three-letter abbreviations (PLD, WHM, …); FFLogs returns PascalCase names (`DarkKnight`), normalized by `JobMap` in `ThresholdPoller`.

### Components and wiring (`Plugin.cs`)

- `Plugin` is the composition root. Dalamud services are injected as **static** `[PluginService]` properties on `Plugin` — accessed globally as `Plugin.Log`, `Plugin.ChatGui`, `Plugin.Framework`, `Plugin.Condition` throughout the codebase (not passed via constructor).
- `FightThresholdManager` — owns fight/bracket lookups; `Adapt()` seeds the hardcoded fight list (M9S, M10S, M11S, M12S phases, DMU) into config on startup.
- `CombatEvent` (`Event/`) — subscribes to `ICondition.ConditionChange`; on `InCombat` flips it calls `ThresholdPoller.Start()`/`Stop()`, but only when the poller is armed (`State != PollerState.None`).
- `ThresholdPoller` (`Threshold/`) — the engine. A background `Task.Run` loop ticks every 1s: computes elapsed pull time, finds the matching kill-time bracket, and fetches data from FFLogs (OAuth2 client-credentials token, then two GraphQL queries: locate the in-progress fight, then its DamageDone table up to `fightStart + bracket ms`). rDPS = `totalRDPS / ((combatTime − damageDowntime) / 1000)`. Note the API lags ~10s, hence the built-in delay before each fetch.
- `ThresholdVerdict` — compares players to their job threshold (≥0, or within 1% under → "above"), then prints KEEP/WIPE to echo chat. Party-chat announcement goes through `UIModule.Instance()->ProcessChatBoxEntry` (sends real `/p` messages) and **must** run on the framework thread via `Plugin.Framework.RunOnFrameworkThread`.
- `Windows/` — ImGui windows (`MainWindow` = start/stop + live rDPS table; `ConfigWindow` = FFLogs credentials + threshold editor). Both read poller state directly every frame.

### State machine and threading gotchas

`PollerState`: `None` (idle) → `WaitingForPull` (armed via UI button) → `Polling` (combat started). Combat end → `Stop()` returns to `WaitingForPull`.

- Polling and FFLogs HTTP calls run on background threads; UI and verdict code read the shared mutable state (`ReportSnapshot`, `State`, `LastBracket`) without synchronization — don't add assumptions about thread affinity.
- Any fetch failure logs a warning and sets `ReportSnapshot = null`; the UI shows a "waiting for data" message. `TimeInCombat` uses `DateTime.Now` deltas, not game time.
- `DowntimePeriod` start/end and bracket strings are `m:ss` text parsed ad hoc (`ParseBracketToMs`); `M9S`-style formatting is expected by lookup (`GetBracket` compares exact strings), so always format seconds as two digits (`{seconds:D2}`).
