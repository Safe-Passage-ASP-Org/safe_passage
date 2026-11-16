# Safe Passage Repository Agent Guidelines

1. Always verify that the `jira-secu` MCP server is authenticated to `https://safepassageaspnet.atlassian.net` (Project `SCRUM`, Board `1`, 2-day sprint cadence) before starting work.
2. Every time you finish a task or code change, commit and push to its Jira-keyed branch (or `development` when integrating) using the active org maintainer Git profile, then transition the Jira ticket accordingly.
3. If creating a new branch in Jira is needed, create a matching `SCRUM-<id>-<short-description>` branch.

## CI/CD & Deployment Verification

- GitHub Actions workflow: `.github/workflows/ci-cd.yml` runs on every `push` and `pull_request` to `development` and `main`.
- It validates:
  - `.NET 10` backend unit tests (`backend-tests/SafePassage.Api.Tests`) and `frontend-web` publish
  - `mobile` Jest tests and `npx expo export --platform web` (Vercel build)
  - Docker builds for `backend/Dockerfile` and `frontend-web/Dockerfile` (Render builds)
  - Live HTTP `200` health checks on:
    - `https://safe-passage-api.onrender.com/api/status`
    - `https://safe-passage-web.onrender.com/`
    - `https://safepassage-mobile.vercel.app/`
- After pushing, check CI results with `gh run list --limit 5` and inspect any failure with `gh run view <run-id> --log-failed` before marking a ticket Done.
