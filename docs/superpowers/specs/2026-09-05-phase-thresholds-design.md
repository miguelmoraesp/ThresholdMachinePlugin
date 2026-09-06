# Phase-Based Thresholds — Design Spec

**Date:** 2026-09-05
**Status:** Approved (design presented in chat and approved by Miguel)
**Scope:** Add per-phase threshold checking, driven by FFLogs-observed phase transitions, as a per-fight opt-in. Motivated by Dancing Mad (Ultimate) joining the fight list alongside dynamic downtime subtraction: users want the fight thresholded per phase, not over the full pull.

## Decisions (locked during brainstorming)

1. **Recognition:** FFLogs `phaseTransitions` on the in-progress fight (the plugin already polls FFLogs every 1s), with user-configured fallback start timestamps used until an observation arrives.
2. **Verdicts:** live table during the phase; one KEEP/WIPE verdict per phase, fired when the phase's end becomes observed.
3. **Scope:** per-fight opt-in via `UsePhases`. DMU is the only fight using it today; all other fights keep today's kill-time brackets untouched (no migration).
4. **Final phase:** a wipe means the last phase's end never arrives as a transition. On `Stop()`, one delayed final fetch (~15s, past the ~10s ingestion lag) resolves all phases and announces any not-yet-announced phase whose end is now known, including the final one. The delayed fetch is cancelled by a fresh `Start()` and logs-and-swallows failures.

## Data model (`Configuration.cs`, additive — no migration, Version stays 0)

```csharp
[Serializable]
public class FightPhase
{
    public string Name { get; set; } = "";            // "Phase 1", "P2 Intermission", …
    public string FallbackStart { get; set; } = "";   // "m:ss" from pull start; "" = none
    public List<JobThreshold> Thresholds { get; set; } = new();
    public List<DowntimePeriod> Downtime { get; set; } = new();
}
// Fight gains:
public bool UsePhases { get; set; } = false;
public List<FightPhase> Phases { get; set; } = new();
```

- Config phase order corresponds to FFLogs absolute phase order: config phase i (0-based) ↔ `phaseTransitions` entry with `id == i + 1` (`PhaseTransition.id` is 1-indexed absolute; `startTime` is report-relative ms). Internally the plugin keys everything 0-based and converts the API's 1-based ids on ingestion.
- Existing DMU `KillTimeBrackets` remain in config and are ignored while `UsePhases` is on; the user can toggle back at any time.
- Threshold 0 keeps today's meaning: job not evaluated.
- Window edges are `m:ss` strings formatted with two-digit seconds everywhere (same convention as brackets — exact-string parsing depends on it).

## PhaseResolver (new `Threshold/PhaseResolver.cs`, pure static, no Dalamud deps)

Input: the fight's `List<FightPhase>`, observed transitions as a map of 0-based absolute phase index → report-relative start ms (converted from the API's 1-based `id` at ingestion), `fightStart` (report-relative ms), `now` (report-relative ms).
Output: per phase — resolved `[startMs, endMs]` (null when not yet started / still live), `StartObserved`, `EndObserved` flags.

Rules:

- **Phase start** = observed transition start if reported; else fallback `m:ss`; else previous phase's resolved end; else fight start (phase 1).
- **Phase end** = next phase's start with observed beating fallback; null when the next boundary is unknown → the phase is still live.
- **Current phase** = the highest index whose resolved start ≤ now.
- Observed beats fallback everywhere: if the party transitions at 4:22 against a 4:30 fallback, the phase-1 verdict uses the real 4:22 window; the fallback only governs the live table during ingestion lag.

## Poller (`ThresholdPoller.cs`) — bracket path untouched

- The existing `fights` query gains `phaseTransitions { id startTime }` — no extra API calls.
- `FetchCurrentPullData`: if the active fight `UsePhases` → resolve the current phase via PhaseResolver → table query over `[phaseStart, now]` (report-relative; observed starts are already report-relative) instead of `[fightStart, fightStart + bracketMs]`. Same rDPS math — `combatTime`/`damageDowntime` come back scoped to the requested window, so dynamic downtime subtraction works per phase for free.
- `ReportSnapshot` gains phase context (index, name, resolved window, window times) so the UI and the verdict read one consistent object.
- Verdict trigger: each poll tick, if a phase's `EndObserved` flipped false → true and that phase has not been announced yet, evaluate and announce it exactly once, using the snapshot measured over its full `[start, end]` window. Live polls never announce.
- `ThresholdVerdict` is generalized to take `(label, List<JobThreshold>)` instead of a `KillTimeBracket`; both engines feed it. Message format: `KEEP! [Phase 2 · 4:22–6:10] 7 players above threshold!` (party-chat flow unchanged, still gated on `AnnounceInPartyChat`).
- Poller exposes `LastPhase` (current `FightPhase`) alongside `LastBracket` for the main window.
- Delayed pull-end pass: scheduled from `Stop()`, cancellable by `Start()`, resolves all phases from the final fights query and announces un-announced phases with known ends, using fallback windows when no transition was ever observed.

## FightThresholdManager

- `AddPhase(fightId)` — appends "Phase N" with the full job list at 0 (mirrors `AddBracket`).
- Lookup helpers for phases; bracket helpers untouched.

## UI

**MainWindow**

- Phase mode adds a status line: `Pull 5:12 · Phase 2 (since 4:22)`; pull timer stays.
- Table reads the current phase's thresholds via `LastPhase`; rows filtered by threshold > 0 exactly as today.
- Unresolvable phase → status text `Waiting for phase data…`.

**ConfigWindow (Thresholds tab)**

- Per-fight checkbox **"Phase-based thresholds"** (`UsePhases`). Off → today's bracket editor, unchanged. On → phase editor:
  - Each phase: Name input, fallback start (mm:ss inputs, bracket-style), Remove, per-role threshold grid (`DrawRoleRow` refactored to take `List<JobThreshold>` + an id instead of `KillTimeBracket`).
  - Downtime rows per phase (start/end mm:ss, add/remove) via a shared downtime widget. The widget is also wired into the brackets editor, which finishes the currently dead `newDowntime` UI (declared but never drawn).
  - `+ Add Phase` button.
- Extra observed transitions beyond the configured phases appear in the live view but get no verdict (no thresholds configured).

## Degradation ladder

1. Transitions observed → exact windows; verdict on observation.
2. Transition not yet ingested (~10s lag) → fallback starts drive the live table; verdict waits for the observation.
3. FFLogs never parses DMU phases → fallback timestamps carry everything; phase verdicts all land in the delayed pull-end pass.
4. No observation and no fallback → phase unresolvable: `Waiting for phase data…`, no verdict for that phase.

HTTP/GraphQL failures keep today's path: warning log, `ReportSnapshot = null`, UI shows the waiting message.

## Testing

- New minimal xunit project `ThresholdMachine.Tests` covering `PhaseResolver` only (pure, all the correctness risk): observed-beats-fallback precedence, current-phase selection, end-observed flips, missing-fallback chains, config-phase ↔ transition-id mapping, `m:ss` parse/format round-trips. TDD applies to the resolver.
- Manual verification checklist (goes in the implementation plan): M9S bracket regression, DMU live pull with real report code, wipe-at-final-phase delayed verdict, `Waiting for phase data…` path, bad-credentials error paths.

## Files touched

`Configuration.cs` · `Threshold/PhaseResolver.cs` (new) · `Threshold/ThresholdPoller.cs` · `Threshold/ThresholdVerdict.cs` · `Threshold/FightThresholdManager.cs` · `Windows/MainWindow.cs` · `Windows/ConfigWindow.cs` · `CLAUDE.md` (architecture section update) · new `ThresholdMachine.Tests` project.

## Out of scope

- Converting all fights to phases (rejected: changes cumulative-checkpoint semantics of M9S–M12S).
- In-game combat-log phase detection (rejected for now; new subsystem + ability IDs).
- Raw-events-based transition detection (rejected: duplicates `phaseTransitions` once parsing exists).
- Assembly version bump happens at release time, not in this change.
