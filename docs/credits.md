# Credits

Hermes-Desktop is built by **VyreVault Studios** and stands on the shoulders of a
number of upstream projects. The credits below cover the substantive design and
UX inspirations that informed our current feature set. The full list of vendored
third-party libraries lives in the published NOTICE alongside each release.

## Inspirations / ported concepts

### `fathah/hermes-desktop` (MIT)

Several Tier-1 and Tier-2 surfaces in this app — the streaming chat primitives,
the slash command palette, the token-usage footer, the Winget release flow, the
MemoryPage editor, the skills install/toggle UI, the saved-model registry, and
the Welcome/Setup first-run wizard — were prototyped against, or directly
inspired by, the Electron/React Hermes Desktop at
<https://github.com/fathah/hermes-desktop>. None of that project's source ships
in this repository; we ported concepts and UX, not code.

That repository is licensed under the MIT license:

```
MIT License

Copyright (c) 2026 github.com/fathah

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.
```

If you maintain that project and feel any part of the port needs additional
attribution or a different framing, please open an issue and we will update
this document.

### `anthropics/anthropic-cookbook` and Anthropic public docs

The MCP-related design — URL trust policy, the activity timeout / timeline UI,
the approval surface — references concepts and examples published by Anthropic
under their public docs and the Anthropic Cookbook.

### Microsoft `microsoft/winget-cli` schema and templates

The Winget manifest layout, version-template structure, and operator runbook
guidance (`docs/winget-submission.md`) follow the official
`microsoft/winget-pkgs` schema and submission flow.

---

## Updating this document

When porting or vendoring a new external surface, add a short section here with:

1. The upstream project's URL.
2. Its license (we only port from permissive licenses such as MIT, Apache-2.0,
   BSD-2/3, and ISC).
3. A one-sentence description of what was inspired by or copied from it.

Keep the copy short. The goal is honest attribution, not a marketing exercise.
