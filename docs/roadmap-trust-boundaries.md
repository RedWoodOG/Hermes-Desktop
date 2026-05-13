# Roadmap trust boundaries and pre-build gates

Companion to [ROADMAP.md](../ROADMAP.md) and the upstream-informed options plan. **Read before starting** work on auto-updates, MCP command center, localhost IDE API, semantic memory, task board, or diagnostics expansion.

## Trusted computing base (who must be trusted)

| Actor | Trust assumption | If violated |
|-------|------------------|-------------|
| Windows user running Hermes | Owns machine and `%LOCALAPPDATA%\hermes` | N/A |
| Hermes Desktop process | Loads config, runs agent, hosts UI | Malware in same user can attach |
| Other **local** processes | May call `127.0.0.1` listeners or read crash dumps | **Treat localhost API as authenticated**, not anonymous-safe |
| MCP servers (stdio/SSE) | Arbitrary code / network from child process | Permission gates + timeouts + scanner on tool I/O |
| Update CDN / GitHub Releases | Delivers binaries | Signature + hash verify before replace |
| VS Code extension (future) | Sends workspace text to Hermes | Publisher trust + minimal scopes |
| UIA / automation targets | Third-party apps | Elevation and integrity policy |

## Non-negotiables (from product strategy)

- **Local-first:** Core chat and agent loop work without cloud relay or gateway daemon.
- **Tool tax:** New or extended tools go through **permissions**, **SecretScanner on outputs**, and **transcript fidelity** — or they do not ship.
- **No split-brain:** Streaming and non-streaming paths must not diverge on permissions, redaction, or recording.
- **Default secure:** Listeners loopback-only unless explicitly opted in; release builds do not log raw prompts/secrets by default.

## Pre-begin gates by roadmap bundle

Use this as a PR checklist when the bundle is selected.

| Bundle | Minimum gates before first merge |
|--------|-----------------------------------|
| **A — Trust / reliability** | Redaction policy for diagnostics; negative tests for SSRF/secret leakage on web tools; staleness/diff UX must not echo secrets into chat. |
| **B — Auto-updates** | Signed payload, hash verify, monotonic version, atomic install + rollback on failed health check; documented skip for unofficial builds. |
| **D — MCP command center** | OAuth: state/nonce/PKCE as applicable; redirect allowlist; MCP timeouts/size caps; scanner on MCP tool results. |
| **E — IDE bridge** | Auth on every mutating API; secret in OS vault not plaintext; CSRF / confused-deputy mitigations for browser → localhost; extension minimal permissions. |
| **F — Windows automation** | Document elevation/sandbox constraints; no silent high-privilege automation. |
| **G — Semantic memory** | Retention, export, delete scope; retrieval abuse / prompt-injection mitigations. |
| **C — Task board / SQLite** | Migration tests + rollback / backup story for new tables. |

## Combined surfaces

If **B + E** or **D + E** ship in the same release window, schedule one **combined attack-surface review** (shared listeners, tokens, third-party packages).

## References

- [HERMES_DESKTOP_FRAMEWORK_STRATEGY.md](internal/HERMES_DESKTOP_FRAMEWORK_STRATEGY.md)
- [roadmap-decisions.md](roadmap-decisions.md) — record which bundles were selected and scores.
