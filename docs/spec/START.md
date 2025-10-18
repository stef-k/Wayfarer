# START (Agent Kickoff)

Use **spec/prompts.md** as the authoritative, binding instructions. Do not deviate.

Policy Gate:
- Read `spec/policies.md` now and follow it for all decisions.

Steps:
1) Read the design list in `spec/prompts.md`.
2) Output first: `PLAN: <N> tasks, ~<T>h each, reason: <short rationale>`.
3) Generate tasks under `spec/tasks/` using `spec/template.task.md` and update `spec/index.yml`.
4) After planning, check `spec/settings.yml`:
   - If `auto_execute_after_planning: true`, continue with `spec/EXECUTE.md`.
   - Otherwise, STOP and wait for manual start.

If you cannot write files, return each file as a Markdown code block prefixed with its exact path.
