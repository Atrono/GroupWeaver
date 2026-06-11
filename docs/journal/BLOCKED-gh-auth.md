# BLOCKED: gh auth login (human required)

- **Symptom:** `gh auth status` → "not logged into any GitHub hosts". The repo
  exists locally only; nothing can be pushed.
- **Attempts:** none possible — CLAUDE.md marks this as the single manual
  prerequisite (Claude cannot mint credentials).
- **Resolution (human):** run `gh auth login` (PAT with `repo` + `workflow`
  scopes). The GitHub account must be the pseudonymous one — no real-name
  exposure in any public artifact (project rule: identity = "Atrono").
- **Then (any session):** `gh repo create GroupWeaver --public --source . --push`
  (`groupweaver-app` if the name is taken), enable Actions, delete this file.
