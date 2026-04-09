---
title: Skill System
type: system
tags: [skills, tools]
created: 2026-04-09
updated: 2026-04-09
sources: [src/skills/skillmanager.cs, src/skills/SkillsHub.cs]
---

# Skill System

Markdown-based custom agent capabilities with YAML frontmatter.

## Skill Format

```markdown
---
name: skill-name
description: One-line description
tools: read_file, write_file, bash
model: optional-model-override
---

System prompt instructions for the skill...
```

## SkillManager

Constructor: `SkillManager(string skillsDir, ILogger<SkillManager> logger)`

On init, recursively loads all `*.md` files from skillsDir with `SearchOption.AllDirectories`.

Key methods:
- `GetSkill(name)` -- lookup by name from ConcurrentDictionary
- `ListSkills()` -- all loaded skills
- `InvokeSkillAsync(name, query, ct)` -- builds context string with skill instructions + user query
- `CreateSkillAsync(name, description, systemPrompt, tools, model?, category?, ct)` -- validated creation
- `EditSkillAsync(name, newContent, ct)` -- full rewrite with rollback
- `PatchSkillAsync(name, oldText, newText, replaceAll, ct)` -- targeted find-and-replace
- `DeleteSkillAsync(name, ct)` -- removes file and cache entry

## Validation (upstream patterns from skill_manager_tool.py)

- Name: `^[a-z0-9][a-z0-9._-]*$`, max 64 chars
- Description: max 1024 chars
- Content: max 100,000 chars
- Duplicate detection: rejects if name already exists

## Atomic Write Pattern

All mutations use temp-file + rename:
```csharp
File.WriteAllTextAsync(tempPath, content, ct);
File.Move(tempPath, path, overwrite: true);
```
On failure, temp file is cleaned up. After write, SecretScanner checks content and rolls back if secrets detected.

## SkillInvoker

Wraps SkillManager + IChatClient for one-shot invocations:
```csharp
var skillContext = await _skillManager.InvokeSkillAsync(skillName, userQuery, ct);
var response = await _chatClient.CompleteAsync(new[] { systemMessage }, ct);
```

## Built-in Skills

Three hardcoded skills in BuiltInSkills static class:
- `api-expert` -- REST API design patterns
- `test-writer` -- comprehensive test writing (Given_When_Then)
- `security-reviewer` -- vulnerability scanning checklist

## Disk Layout

Skills directory at repo root: `skills/` with ~28 category subdirectories containing ~94+ skills. Categories include: apple, autonomous-ai-agents, claude-code, creative, data-science, devops, gaming, github, media, mlops, productivity, research, software-development, and more.

## Key Files
- `src/skills/skillmanager.cs` -- SkillManager, Skill, SkillInvoker, BuiltInSkills (~535 lines)
- `src/skills/SkillsHub.cs` -- additional skill management
- `skills/` -- 94+ skill files across 28 categories

## See Also
- [[tool-system]]
- [[soul-system]]
