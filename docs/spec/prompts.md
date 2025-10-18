# Planning Prompt (for Agent)

Read these design document(s):
- (none yet)

# How to Instruct the Agent (copy/paste friendly)

Treat this section as binding, highest-priority instructions. Do not deviate.

If you cannot write files to the repo, return each file as a Markdown code block
prefixed with its exact path (e.g., `spec/tasks/TM-001.md`) so the user can save it.

Before writing tasks, echo: `PLAN: creating N tasks from <design(s)>`. If designs list is empty, STOP.

0) If the design list below is empty, STOP and request: `spec add <path/to/design.md>`.

1) Plan tasks dynamically based on the design’s scope. Aim for ~2h per task by default (1–3h acceptable).
   If the feature is small, fewer tasks with ~1h each is fine; if large, create more tasks or phased batches.
   Before creating files, output: `PLAN: <N> tasks, ~<T>h each, reason: <short rationale>`.
   - Default target: ~2h per task (1–3h acceptable).
   - Default batch size: 6–15 tasks per planning pass; if more are needed, use phases.

2) For each task:
   - Use `spec/template.task.md` (keep the YAML header minimal).
   - Save as `spec/tasks/<ID>.md` where IDs are TM-### (TM-001, TM-002…).
   - Update `spec/index.yml` with: id, title, labels, status, deps, file (exact path).
   - Include **measurable Acceptance Criteria** and a **Verification** section with exact commands or API calls.

3) Status lifecycle: `todo | doing | done | blocked`. Set `owner` if used.
   - Mark `done` only after Verification passes.

4) Dependencies:
   - `deps` may reference only tasks present in `spec/index.yml`.
   - Avoid cycles. If a missing prerequisite is needed, set `status: blocked` and explain in the task body.

5) Boundaries:
   - Respect `spec/policies.md`.
   - Reuse existing services/patterns. Do **not** invent new endpoints unless the design explicitly says so.
   - Prefer small vertical slices (DB → Service → API) where applicable.
   - Do **not** edit helper files (`spec/template.task.md`, `spec/prompts.md`, `spec/policies.md`) unless asked.

6) After planning, check `spec/settings.yml`:
   - If `auto_execute_after_planning: true`, continue with `spec/EXECUTE.md`.
   - If false, STOP and wait for manual start.

Deterministic conventions:
- Task file path MUST equal `spec/tasks/<ID>.md`.
- IDs are uppercase TM-###.
- `deps` reference only known tasks; if blocked by missing tasks, set `status: blocked` and explain why in the task body.


## Output required
- Create or update `spec/index.yml` (flat list of tasks).
- Write task files to `spec/tasks/` using `spec/template.task.md`.
- Keep tasks small, vertical slices (DB → Service → API) where applicable.
