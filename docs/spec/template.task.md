---
id: TM-XXX
title: Short action name
status: todo           # todo | doing | done | blocked
labels: []             # e.g., db, backend, api, ui, sse, auditing, docs
deps: []               # other task IDs
owner: ""              # optional, set when taking the task
inputs:
  - <DESIGN_DOC_PATH>  # pick one or more paths from spec/index.yml:designs
outputs: []
timebox: "2h"
---

## Summary
One or two sentences of what must be built, referencing the design doc.

## Acceptance Criteria (measurable)
- [ ] Explicit, testable check #1
- [ ] Explicit, testable check #2
- [ ] Explicit, testable check #3

## Steps
- [ ] Read inputs
- [ ] Implement …
- [ ] Add/adjust tests
- [ ] Run verification
- [ ] Update spec/index.yml (status, deps if changed)

## Verification
Exact commands or API calls to prove success.

## Notes
Reuse existing services; do not invent endpoints unless the design says so.
