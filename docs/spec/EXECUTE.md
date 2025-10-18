# EXECUTE (Task Implementation Instructions for Agent)

Follow these steps exactly.

Policy Gate:
- Read `spec/policies.md` now and follow it for all code, tests, data handling, and commit/PR behavior.

0) Read `spec/settings.yml`.
   - If `settings.require_owner_for_doing: true`, set `owner` in the task file before switching to `doing`.
   - **Branching is mandatory if `settings.enforce_branching: true`.**
   - Branch pattern hint: `settings.branch_naming` (e.g., feat/{id}-{slug}).
   - Protected branches: `settings.protected_branches` (NEVER commit here).

1) Select a task:
   - Read `spec/index.yml` and pick the first `status: todo` whose `deps` are all `done`.

2) Take the task:
   - Open `spec/tasks/<ID>.md`.
   - If required by settings, set `owner: <name>`.
   - Change `status: doing`.

3) **Create a working branch (REQUIRED when enforce_branching = true):**
   - Compute `<slug>` from the task title (lowercase, hyphenated, alnum+hyphen).
   - Branch name = `settings.branch_naming` with `{id}` and `{slug}` replaced.
   - Detect current branch. If it is in `protected_branches`, STOP and output the shell command to create/switch:
     - `git checkout -b <branch>` (if new) OR `git switch <branch>` (if exists).
   - If you cannot run shell commands, **return the exact commands** to run as a fenced code block.

4) Implement per the task’s Summary + Acceptance Criteria.
   - Reuse existing services/patterns. Do not invent endpoints beyond the design.
   - Keep changes scoped to the task.

5) Tests & Verification:
   - Add/adjust tests as required.
   - Run the **Verification** steps from the task and ensure they pass.

6) Commit:
   - Commit message = `settings.commit_message_template` with `{id}` and `{title}` replaced.
   - If on a protected branch, STOP and output the correct branch/switch commands instead.

7) Complete:
   - Check off Acceptance Criteria in the task file.
   - Flip `status: done` when verification passes.
   - Update `spec/index.yml` entry for `<ID>` with the final `status` (and `owner` if used).

8) Loop behavior (from settings):
   - If `settings.next_task_loop: auto`, repeat from step 1.
   - If `manual`, STOP and wait.

**If you cannot write files or run shell:**
Return each changed file and each required shell command as Markdown code blocks with exact repo paths/commands.
