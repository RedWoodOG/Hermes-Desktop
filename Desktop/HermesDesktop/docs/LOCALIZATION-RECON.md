# Localization recon: hardcoded strings → `.resw` (without painting into corners)

This document is a **reconnaissance and research summary** for migrating Hermes Desktop from hardcoded UI copy to **MRT / `Resources.resw`** + **`x:Uid`** / **`ResourceLoader`**, aligned with [Microsoft’s MRT string localization](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/mrtcore/localize-strings) and the repo’s [globalization instructions](../.github/instructions/globalization.instructions.md).

---

## Current state (inventory)

### Already localized

| Area | Mechanism |
|------|-----------|
| `MainWindow.xaml` nav | `x:Uid` → `Strings/en-us/Resources.resw` (+ `zh-cn` satellite) |
| `IntegrationsPage.xaml` (partial) | `x:Uid` |
| `SettingsPage.xaml` (small footer block) | `x:Uid` for path-related buttons/labels |
| `ChatPage.xaml.cs`, `DashboardPage.xaml.cs`, `SettingsPage.xaml.cs`, `IntegrationsPage.xaml.cs`, `MainWindow.xaml.cs` | `ResourceLoader.GetString(...)` for some runtime strings |

### Heavily hardcoded (high volume)

| File / area | Notes |
|-------------|--------|
| **`SettingsPage.xaml`** | **Largest surface** (~160+ `Text="` / `Content="` / `PlaceholderText="` hits). Labels, section titles, `ComboBoxItem` labels, helper copy, buttons. |
| **`DashboardPage.xaml`** | KPI cards, section headers, buttons (`Test Connection`, `Open Chat`), empty state, system paths section. |
| **`ChatPage.xaml`** | Header (`Hermes Agent`, `New Chat`), `Reasoning` expander label, thinking line, input chrome. **DataTemplate** contains `Text="Reasoning"` — special case (below). |
| **Other pages / panels** | `AgentPage`, `MemoryPage`, `BuddyPage`, `SkillsPage`, `SessionPanel`, `AgentPanel`, `TaskPanel`, `ReplayPanel`, `MemoryPanel`, `SkillsPanel`, `FileBrowserPanel`, `BuddyPanel`, `ToolCallCard`, `ApprovalCard`, `PermissionDialog`, `CodeBlockView`, `IntegrationsPage` (remainder). |

### Hardcoded in code-behind (must move to `ResourceLoader` or stay format-only)

| Location | Examples |
|----------|----------|
| `SettingsPage.xaml.cs` | Validation + save status: `"Model name is required."`, `"Saved successfully..."`, etc. |
| `DashboardPage.xaml.cs` | `TestConnectionResult`: `"Testing..."`, `"Not configured"` |
| `ChatPage.xaml.cs` | `SessionIdLabel`: `"New Session"` |
| `MemoryPage.xaml.cs` | `"Saved!"`, badge `"0"` |
| `BuddyPanel.xaml.cs` / `BuddyPage.xaml.cs` | Error / badge copy |
| `CodeBlockView.xaml.cs` | `"Copied!"` / `"Copy"` |
| `ReplayPanel.xaml.cs` | `Play` / `Stop` (could stay symbolic + localize) |
| `App.xaml.cs` | `ContentDialog`: `Permission Required`, `Allow` / `Deny` |
| `AgentPage.xaml.cs` / `AgentPanel.xaml.cs` | Dialog titles / buttons |

**Rule of thumb:** anything a **translator** or **regional user** should see belongs in `.resw` (or format strings built from `.resw`). Purely **technical tokens** (e.g. internal test prompt `"Reply with exactly: OK"`) can stay in code but should be **commented** and ideally **not** shown in UI.

---

## Official constraints (MRT / `.resw`) — avoid these corners

From Microsoft’s documentation (paraphrased; see link above):

1. **Simple vs property identifiers**  
   - A **simple** name (e.g. `Farewell`) is for **`ResourceLoader.GetString("Farewell")` only**.  
   - **`x:Uid="Greeting"`** requires **property rows** like `Greeting.Text`, `Greeting.Content`, etc.  
   - You **cannot** have both `Farewell` and `Farewell.Text` in the same `.resw` — **duplicate entry** at build time.

2. **Property names must match the control type**  
   - `Greeting.Text` on a **Button** fails at runtime (Button has **Content**, not Text). Use `ButtonGreeting.Content` and `x:Uid="ButtonGreeting"`.

3. **Do not rename resource IDs casually**  
   - Loc vendors track by **resource name**. Renames look like delete+add and **invalidate translations** (`zh-cn`, future locales).

4. **Attached properties in `.resw`**  
   - Use the documented form, e.g.  
     `Greeting.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name`  
   - Needed for screen readers when visible `Text` is not enough.

5. **Segmented names from code**  
   - Dots in logical names map to paths for `GetString`; use **`/` instead of `.`** when calling `GetString` for segmented keys (per docs).

6. **Libraries**  
   - Hosted resources resolve from the **app** PRI. Hermes Desktop should own all UI strings; **Hermes.Core** should not introduce desktop-only `.resw` unless you explicitly merge PRI (not recommended for this app).

---

## Safe patterns (recommended)

### A. XAML static labels

- Add **`x:Uid="UniqueStableName"`** on the element.  
- In **`Strings/en-us/Resources.resw`**, add **`UniqueStableName.Text`**, **`UniqueStableName.Content`**, **`UniqueStableName.PlaceholderText`**, etc.  
- Mirror keys in **`Strings/zh-cn/Resources.resw`** (or other locales) with the **same names**, translated values.

**Naming:** prefer **page-scoped prefixes** to avoid collisions: `DashboardSessionsLabel.Text`, `ChatHeaderTitle.Text` (matches many existing keys).

### B. `ComboBoxItem` and lists

- **Option 1 (simplest):** each item **`x:Uid`** + `.Content` in `.resw`.  
- **Option 2:** bind `ItemsSource` to a view-model list of **localized** strings (loaded once per culture change). Heavier but good if items are dynamic.

Avoid sharing one `x:Uid` across multiple items.

### C. Code-only and formatted strings

- Add **simple** keys: `SettingsModelSaveSuccess`, `DashboardTestConnectionTesting`.  
- Use **`string.Format(CultureInfo.CurrentCulture, loader.GetString("DashboardPhaseFormat"), phase)`** for **Phase: {0}**-style copy — never concatenate translator sentences in the wrong order.

### D. DataTemplates (`ChatPage` message bubbles)

- Strings like **`Reasoning`** inside **`DataTemplate`** can use **`x:Uid`** on the inner **`TextBlock` / `Expander`**, same as elsewhere.  
- Watch **compiled `x:Bind`**: keep bindings valid; **`x:Uid`** is orthogonal.  
- If a string is **per-role** (e.g. author label), it often comes from **code** today (`ResourceLoader` in `ChatPage.xaml.cs` is already the right place).

### E. Runtime overrides vs `x:Uid`

- **MRT applies at load.** If code sets **`SomeBlock.Text = "..."`** after load, it **replaces** the localized value until set again.  
- For controls that **toggle** text (e.g. gateway **Start/Stop**), either:  
  - set text from **`ResourceLoader` in both states**, or  
  - use **two** resource keys and swap between them.

### F. Culture changes (no in-app toggle today)

- OS **display language** drives PRI. If you add an **in-app language toggle** later, you will need **`ResourceContext`** override and a **refresh** pass over UI — planning **stable resource names** now still helps.

---

## What *not* to do (corners)

| Pitfall | Why |
|---------|-----|
| Same base name for simple + `.Text` | `.resw` **duplicate name** build break |
| `x:Uid` pointing at **`.Text`** for a **Button** | Runtime error / wrong property |
| Huge **monolithic** PR touching all XAML | Hard to review; easy to miss `zh-cn` parity |
| Embedding **exception messages** in user-visible tool output | Security / UX; log separately (see prior review) |
| **Hardcoding punctuation** that differs by locale | Prefer full sentences in `.resw` per language |

---

## Suggested migration order (low risk → high risk)

1. **Small pages / controls:** `PermissionDialog`, `CodeBlockView`, `IntegrationsPage` gaps, `ChatPage` header + thinking line (visible, few strings).  
2. **`DashboardPage.xaml`** + **`DashboardPage.xaml.cs`** status strings together.  
3. **Remaining panels** (`AgentPanel`, `MemoryPanel`, …).  
4. **`SettingsPage.xaml` + `.cs` last** — largest; do in **vertical slices** (e.g. “User Profile” section + its code-behind messages in one PR).

Each PR should:

- Update **`en-us`** and **`zh-cn`** (or explicitly document “zh-cn follow-up”) so PRI stays consistent.  
- **`dotnet build`** `HermesDesktop` (Release) before merge.

---

## Verification checklist

- [ ] No new **simple + `.Property`** name collision in `.resw`  
- [ ] Every new **`x:Uid`** has matching **`.resw`** rows for **each** localized property  
- [ ] **Formatted** strings use **`CultureInfo.CurrentCulture`** (or invariant only for machine protocol text)  
- [ ] **Automation** strings for icon-only buttons use **attached property** rows where needed  
- [ ] Change Windows display language to **中文** and smoke-test **nav + changed pages**  
- [ ] Grep for remaining **`Text="` / `Content="`** in touched files to zero intentional literals

---

## Quick grep commands (maintenance)

From repo root (examples):

```powershell
rg 'Text="' Desktop/HermesDesktop --glob '*.xaml'
rg 'Content="' Desktop/HermesDesktop --glob '*.xaml'
rg 'PlaceholderText="' Desktop/HermesDesktop --glob '*.xaml'
```

For C# literals:

```powershell
rg '\.(Text|Content)\s*=\s*"' Desktop/HermesDesktop --glob '*.xaml.cs'
```

---

## References

| Topic | URL |
|-------|-----|
| Localize strings (MRT, `.resw`, `ResourceLoader`, manifest) | https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/mrtcore/localize-strings |
| `x:Uid` directive | https://learn.microsoft.com/en-us/windows/apps/develop/platform/xaml/x-uid-directive |
| Globalization checklist | https://learn.microsoft.com/en-us/windows/apps/design/globalizing/guidelines-and-checklist-for-globalizing-your-app |
| Resource qualifiers (`en-us`, `zh-cn`, …) | https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/mrtcore/tailor-resources-lang-scale-contrast |

Repo: **`Desktop/HermesDesktop/.github/instructions/globalization.instructions.md`**
