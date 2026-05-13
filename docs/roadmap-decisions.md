# Roadmap bundle decisions

**Purpose:** Record which option bundles from the upstream-informed roadmap are **in scope** for the next cycle, with scores and dates. Stops “oral tradition” drift.

## Scoring (1–5 each)

| Criterion | Question |
|-----------|----------|
| User pain | Fixes a top complaint or competitive gap in [ROADMAP.md](../ROADMAP.md)? |
| Differentiation | Stronger on Windows / native desktop than a generic agent? |
| Upstream drift | Would we regret ignoring this if upstream doubles down? |
| Cost | Engineering + maintenance (extension > settings > core tweak)? |
| Security debt | Blast radius if wrong (listeners, OAuth, updates)? |
| Privacy / telemetry | What appears in logs or “copy diagnostics”? |
| Operability | Rollback, kill-switch, offline behavior? |
| Framework fit | Aligns with [HERMES_DESKTOP_FRAMEWORK_STRATEGY.md](internal/HERMES_DESKTOP_FRAMEWORK_STRATEGY.md) — not checklist parity? |

**Bundles:** A Trust/reliability · B Auto-updates · C Task board + diffs · D MCP command center · ~~E IDE bridge~~ *(explicitly out of scope for this track)* · F Windows superpowers · G Semantic memory.

---

## Decision log

| Date | Bundles selected | Scores (optional) | Owner | Notes |
|------|------------------|-------------------|-------|-------|
| 2026-05-08 | **A, B, C, D, F, G** (all except **E**) | — | Product | IDE bridge deferred — no localhost editor API / VS Code extension in this roadmap wave. Ship order below. |

### Recommended execution order (trust → shipping → MCP → moat → orchestration → memory)

1. **A** — Trust / reliability (diagnostics, diffs-in-feed, secrets/SSRF hardening)  
2. **B** — Auto-updates (Releases + MSIX channel)  
3. **D** — MCP command center (discovery, OAuth, health, settings)  
4. **F** — Windows superpowers (UIA, Sandbox, shell depth)  
5. **C** — Task board + change review *(SQLite migrations: use [roadmap-trust-boundaries.md](roadmap-trust-boundaries.md) gate row)*  
6. **G** — Semantic memory / compaction *(same: migration + privacy gates)*  

Re-score or re-order per quarter if staffing or risk changes; **E** can be re-opened later with a separate API contract freeze doc.

---

## Vertical slice — Definition of Done (template)

For each chosen bundle, first slice must include:

1. **Tests:** `HermesDesktop.Tests` (MSTest) for logic; Windows smoke for WinUI/MCP/UIA/update as applicable.
2. **Negative tests** for **B** and **D** (and any future listener): auth failure, tampered signature, SSRF probe where relevant.
3. **Localization:** New UI strings in `en-us` and **`zh-cn`** `Resources.resw` ([ROADMAP.md](../ROADMAP.md) contribute section).
4. **Logs:** No raw secrets/prompts in default release logging; document what “Copy diagnostics” includes.
5. **Trust doc:** Re-read [roadmap-trust-boundaries.md](roadmap-trust-boundaries.md) for this bundle’s row.

---

## Optional CI follow-ups (track separately)

- `en-us` / `zh-cn` resw key parity check.
- Release job: assert `HermesDesktop.csproj` `<Version>` matches tag before publishing asset (if not already present on `main`).
- Split release workflow: build+smoke before `softprops/action-gh-release`.
