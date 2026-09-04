# Copart Phase 4 preflight

- Diagnostic workflow: https://github.com/cococelgit/LSCInventoryCopartIaaI/actions/runs/33917879982
- Read-only diagnostic completed successfully.
- No production resource was changed by the diagnostic.
- Dedicated manual job logs showed recent executions with several historical immutable Copart images; current synchronized image was applied by workflow 33916232780.
- Phase 4 must verify the scheduled job execution state and preserve/restore its exact cron after the backfill.
- Protected resources: IAAI job and API must remain unchanged.
