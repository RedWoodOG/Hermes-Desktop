---
name: commit-push-pr
description: Full workflow - commit changes, push to remote, and create a pull request with description.
tools: bash, read_file, grep
---

# Commit, Push, and Pull Request

You are a developer completing the full cycle from local changes to a pull request. This skill handles committing, pushing, and PR creation in one workflow.

## Workflow

### Step 1: Understand the Full Change Set

```bash
git status
git diff --stat HEAD
git log --oneline -5
git branch --show-current
```

Identify:
- Current branch name
- What the base/target branch is (usually `main` or `master`)
- All changes (staged, unstaged, untracked)

### Step 2: Review All Changes

```bash
git diff HEAD
git diff --cached
```

Read through every change. Understand the full scope of what will be in the PR.

### Step 3: Create the Commit

Stage relevant files (specific files, never blindly `git add -A`):

```bash
git add path/to/changed/files
```

Write a clear commit message:

```bash
git commit -m "$(cat <<'EOF'
Descriptive subject line

Body explaining what and why.
EOF
)"
```

If there are multiple logical changes, create multiple commits.

### Step 4: Ensure Branch is Ready

```bash
# Check if branch tracks a remote
git rev-parse --abbrev-ref --symbolic-full-name @{u} 2>/dev/null

# See all commits that will be in the PR
git log main..HEAD --oneline
```

### Step 5: Push to Remote

```bash
git push -u origin $(git branch --show-current)
```

If the branch doesn't exist on the remote yet, this creates it. The `-u` flag sets up tracking.

### Step 6: Create the Pull Request

Use `gh pr create` with a well-structured description:

```bash
gh pr create --title "Short descriptive title" --body "$(cat <<'EOF'
## Summary
- Bullet point describing key change 1
- Bullet point describing key change 2

## Test plan
- [ ] Step to verify the change works
- [ ] Edge case to check
EOF
)"
```

**PR Title Rules:**
- Under 70 characters
- Imperative mood
- Summarize the user-facing or developer-facing impact

**PR Body Rules:**
- Summary section with 1-3 bullet points covering the key changes
- Test plan section with actionable verification steps
- Reference any related issues with `Fixes #123` or `Related to #456`

### Step 7: Report

Output the PR URL and a brief summary of what was included.

## Rules

- NEVER force push to main/master
- NEVER skip pre-commit hooks with `--no-verify`
- NEVER commit secrets, credentials, or .env files
- Always review the full diff before committing
- If the branch is `main` or `master`, create a new feature branch first
- Always set up tracking with `-u` on first push
- Include ALL commits from the branch in the PR analysis, not just the latest one
