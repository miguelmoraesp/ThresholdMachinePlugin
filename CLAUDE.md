# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Dalamud plugin for FFXIV ("Threshold Machine"). During a raid pull it polls the FFLogs GraphQL API, compares each player's live rDPS against per-job thresholds configured for kill-time brackets (e.g. "≤5:30") or, per-fight opt-in, per-phase windows driven by FFLogs-observed transitions, and announces KEEP/WIPE verdicts in echo (and optionally party) chat.

## Build

```powershell
dotnet build -c Release
dotnet test
```

- Requires XIVLauncher with Dalamud dev hooks installed — `Dalamud.NET.Sdk` resolves its references from `%APPDATA%\XIVLauncher\addon\Hooks\dev\` (printed during restore).
- Targets `net10.0-windows7.0` on Dalamud API 15 (`Dalamud.NET.Sdk/15.0.0`); platform is x64. Output: `ThresholdMachine/bin/x64/Release/ThresholdMachine.dll`.
- `ThresholdMachine.Tests` (xunit) covers `PhaseResolver` only — the resolver is pure static with no Dalamud deps so the test project can reference the plugin directly. TDD applies when changing it.
- No lint step. `.editorconfig` governs style (var-preferred, 4-space, LF, camelCase private fields, `On` prefix for event handlers).

## Release process

Bump `AssemblyVersion` in three places together: `ThresholdMachine/ThresholdMachine.csproj` (`<Version>`), `ThresholdMachine/ThresholdMachine.json`, and `repo.json` (which also carries the GitHub release download URL for the Dalamud third-party repo manifest).

## Architecture

Everything lives in `ThresholdMachine/` with one namespace per folder. Flow: **combat event → poller loop → FFLogs GraphQL → verdict → chat**.

### Data model (`Configuration.cs`)

`Configuration` → `FightList` → `Fight` → `KillTimeBracket` (keyed by an `m:ss` string) → per-`JobThreshold` + `DowntimePeriod`s. Downtime windows are subtracted from combat time before computing rDPS. Jobs are identified by three-letter abbreviations (PLD, WHM, …); FFLogs returns PascalCase names (`DarkKnight`), normalized by `JobMap` in `ThresholdPoller`.

Per-fight opt-in alternative: `Fight.UsePhases` + `Fight.Phases` → `FightPhase` (`Name`, `FallbackStart` `m:ss` from pull start, `Thresholds`, `Downtime`). While `UsePhases` is on the fight's `KillTimeBrackets` are ignored (no migration between the two modes). Config phase order maps 1:1 onto FFLogs absolute phase order: config phase i (0-based) ↔ `phaseTransitions` entry with `id == i + 1` (the API's ids are 1-indexed; everything internal is 0-based — converted at ingestion by `PhaseResolver.ParseTransitions`).

### Components and wiring (`Plugin.cs`)

- `Plugin` is the composition root. Dalamud services are injected as **static** `[PluginService]` properties on `Plugin` — accessed globally as `Plugin.Log`, `Plugin.ChatGui`, `Plugin.Framework`, `Plugin.Condition` throughout the codebase (not passed via constructor).
- `FightThresholdManager` — owns fight/bracket lookups; `Adapt()` seeds the hardcoded fight list (M9S, M10S, M11S, M12S phases, DMU) into config on startup.
- `CombatEvent` (`Event/`) — subscribes to `ICondition.ConditionChange`; on `InCombat` flips it calls `ThresholdPoller.Start()`/`Stop()`, but only when the poller is armed (`State != PollerState.None`).
- `ThresholdPoller` (`Threshold/`) — the engine. A background `Task.Run` loop ticks every 1s: computes elapsed pull time and fetches data from FFLogs (OAuth2 client-credentials token, then two GraphQL queries: locate the in-progress fight (with `phaseTransitions { id startTime }`), then its DamageDone table). rDPS = `totalRDPS / ((combatTime − damageDowntime) / 1000)`. Note the API lags ~10s, hence the built-in delay before each fetch. Two modes:
  - **Bracket mode** (default): DamageDone table up to `fightStart + bracket ms`; every fetch announces a verdict.
  - **Phase mode** (`Fight.UsePhases`): `PhaseResolver.Resolve` computes each phase's `[start, end]` window (observed transition start beats the configured `FallbackStart`; a phase with neither boundary is still live). The live table covers `[phaseStart, now]` and never announces. A phase's KEEP/WIPE fires exactly once, when its end becomes *observed* (fallback ends don't trigger mid-fight). On `Stop()` a delayed pass (~15s, past ingestion lag; cancelled by a fresh `Start()`) re-resolves all phases against the ended fight — the last phase ends at the fight's `endTime` — and announces anything not yet announced.
- `PhaseResolver` (`Threshold/`) — pure static, no Dalamud deps, unit-tested in `ThresholdMachine.Tests`. Also owns `m:ss` parse/format (`ParseMs`/`FormatMs`) and the 1-based→0-based transition-id conversion.
- `ThresholdVerdict` — takes `(label, List<JobThreshold>)`; both engines feed it (bracket label is the bracket string, phase label is `Phase 2 · 4:22–6:10`). Compares players to their job threshold (≥0, or within 1% under → "above"), then prints KEEP/WIPE to echo chat. Party-chat announcement goes through `UIModule.Instance()->ProcessChatBoxEntry` (sends real `/p` messages) and **must** run on the framework thread via `Plugin.Framework.RunOnFrameworkThread`.
- `Windows/` — ImGui windows (`MainWindow` = start/stop + live rDPS table; `ConfigWindow` = FFLogs credentials + threshold editor). Both read poller state directly every frame.

### State machine and threading gotchas

`PollerState`: `None` (idle) → `WaitingForPull` (armed via UI button) → `Polling` (combat started). Combat end → `Stop()` returns to `WaitingForPull`.

- Polling and FFLogs HTTP calls run on background threads; UI and verdict code read the shared mutable state (`ReportSnapshot`, `State`, `LastBracket`, `LastPhase`) without synchronization — don't add assumptions about thread affinity.
- Any fetch failure logs a warning and sets `ReportSnapshot = null`; the UI shows a "waiting for data" message. `TimeInCombat` uses `DateTime.Now` deltas, not game time. The one-shot delayed phase pass swallows its own failures (warning log) by design.
- `DowntimePeriod` start/end, bracket, and `FallbackStart` strings are `m:ss` text parsed ad hoc (`ParseBracketToMs` / `PhaseResolver.ParseMs`); `M9S`-style formatting is expected by lookup (`GetBracket` compares exact strings), so always format seconds as two digits (`{seconds:D2}`).
