# Minimal Policies (agents must obey)

## Scope & Authority
- This file is **binding** for all work. Do not deviate.
- When in doubt, ask or propose a small change in a separate task.

## Security & Privacy
- Principle of least privilege. Touch only files/services required by the task.
- Never commit secrets or tokens. Use env vars or the repo’s secret manager.
- Do not log PII/credentials. Redact sensitive fields in logs/errors.
- Private data must remain private; follow the design’s access rules exactly.

## Dependencies & Tools
- **No new frameworks/libs** without explicit approval. Prefer stdlib/existing deps.
- Pin versions and update lockfiles. Avoid global/system installs.
- Do not modify CI/CD config unless the task explicitly says so.

## Code & Git Hygiene
- Follow `spec/settings.yml` for branching (branch pattern, protected branches).
- Small, atomic commits. Include task ID in commit messages/PR titles.
- Don’t push directly to protected branches.

## API, Schemas & Migrations
- Backward compatible by default. Additive changes first; breaking changes require explicit approval.
- Database/schema migrations must be reversible and **idempotent**.
- Separate data migrations from schema migrations where possible.

## Testing & Verification
- Every task must include runnable verification steps (commands or API calls).
- Add/update tests at the appropriate level (unit/integration/e2e) to cover acceptance criteria.
- Tests must pass locally (or in the provided runner) before marking `done`.

## Observability & Ops
- Log at appropriate levels; no secrets/PII in logs.
- Prefer metric/trace hooks only if the project already uses them.
- Use feature flags or configuration, **not** hard-coded constants, for toggles.

## Performance & Reliability
- Respect timeouts and retries; avoid infinite waits.
- Keep operations idempotent where applicable (especially APIs/tasks).
- Mind basic perf budgets (avoid O(n^2) on large inputs; stream for big files).

## UX / Accessibility / i18n (if user-facing)
- Don’t regress accessibility (labels, contrast, keyboard navigation).
- Keep copy externalized if the project already uses i18n.

## Licensing & Attribution
- Use compatible licenses only; preserve existing NOTICE/LICENSE files.
- Attribute third-party code as required by its license.

## External Calls & AI Use
- No new outbound network calls, third-party APIs, or AI services without approval.
- If an LLM is used inside the project, **never** send secrets/PII and follow its safety constraints.

## Documentation
- Update any touched README/config docs when behavior changes.
- Record notable decisions/limitations in the task’s Notes section.

## Project-Specific Additions
- (Add bullets here as the repo grows; keep this file under one page.)
