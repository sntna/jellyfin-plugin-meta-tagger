# Triage labels

| Skill role | GitHub label |
| --- | --- |
| needs-triage | needs-triage |
| needs-info | needs-info |
| ready-for-agent | agent:ready |
| ready-for-human | ready-for-human |
| wontfix | wontfix |

Execution labels are separate: `agent:running`, `agent:review`, `agent:blocked`.
The dispatcher removes `agent:ready` when claiming work. It leaves tickets open
until their reviewed closing PR is merged. `ready-for-human` means genuinely missing human input or access, not browser testing or an explicitly authorized disposable-fixture change. Never requeue a failed ticket merely
by removing its label; inspect its saved run and worktree first.
