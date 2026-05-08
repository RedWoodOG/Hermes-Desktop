## Learned User Preferences
- Prefers concise, triage-oriented engineering reports with ranked, actionable recommendations over generic summaries.
- For reviews, wants actionable findings ordered by severity with file references and no changes unless explicitly requested.
- Values Windows-native desktop agent capability, especially UI Automation that can inspect and drive non-browser apps without per-app APIs.
- Expects substantive fixes (code and tests) for open issues rather than tracking-only or audit-only updates.

## Learned Workspace Facts
- Hermes-Desktop is a WinUI 3/.NET 10 desktop app with Hermes.Core agent runtime, Hermes.Agent CLI, MSIX packaging, and HermesDesktop.Tests.
- Core agent/tool logic lives under `src/`, while the WinUI shell, DI, services, resources, and desktop-only tools live under `Desktop/HermesDesktop/`.
- CI runs MSTest coverage primarily against Hermes.Core on Ubuntu, while Windows desktop validation relies more on builds and smoke probes than automated UI tests.
- `App.xaml.cs`, resource localization, provider streaming/chat paths, MCP transports, and desktop automation are recurring high-risk or high-leverage areas.
- Project docs have recurring drift risk between the root product README and `Desktop/HermesDesktop/README.md`; the root README version line can also run ahead of published GitHub Releases.
- CI (`ci-dotnet-tests.yml`) validates new tests against an allowlisted set of framework/API symbols used in test code; extending tests with additional BCL types may require updating that allowlist.
