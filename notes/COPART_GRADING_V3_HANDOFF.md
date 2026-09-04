# Copart Grading v3 Handoff

## Scope

This change is Copart-only. It changes the Copart policy identifier from `lsc_pre_grade_v2` to `lsc_pre_grade_v3`. IAAI remains on `lsc_pre_grade_v1`; Apibara, IAAI jobs, API deployment, cron, secrets, and database migrations are outside the scope of this patch.

## Changes implemented

| Area | v3 change |
|---|---|
| Policy identity | Copart resolves to `lsc_pre_grade_v3` |
| Score scale | Copart factors are normalized to a 100-point scale |
| Weights | Seller 20, mechanical condition 25, damage 25, title/documentation 15, information quality 15 |
| Seller mapping | `Seller Name` is classified before scoring, with category, taxonomy version, confidence, and evidence persisted |
| Raw evidence | Seller, primary/secondary damage, Runs/Drives, keys, and odometer raw values are preserved in `AdditionalData` |
| Numeric parsing | Comma-separated and unit-suffixed numeric values are handled more safely |
| Key parsing | Explicit variants such as `NO KEYS` and `WITHOUT KEY` are recognized; no inference is made from unrelated text |
| Explainability | Existing factor, penalty, coverage, confidence, missing-field, policy-version, and input-hash fields remain persisted |

## Validation

The .NET solution test suite passes **162 tests**. The tests include v3 scale normalization, seller-name taxonomy mapping, Copart processor/backfill versioning, Run & Drive preservation, and existing IAAI behavior.

## Deployment guardrails

Do not deploy the API. Do not update `ca-lsc-inventory-api-prod`. Do not change `job-lsc-iaai-pilot-prod`, `job-lsc-inventory-ingestion-prod`, Apibara configuration, cron, secrets, or migrations. Promote only the dedicated Copart job image after reviewing the diff and verifying the image/revision before and after.

## Required post-deployment sequence

First promote the Copart job image. Then execute one controlled Copart run, not a full historical backfill. Verify that new score rows have `policy_version = 'lsc_pre_grade_v3'`, that factor JSON contains F01–F05 with the new maximums, and that the raw evidence keys are present. Only after this check should the active-Copart backfill be started.

The backfill must select only active Copart rows, skip rows whose input hash and policy version are already current, and record scanned, scored, skipped, failed, and remaining counts. It must not select IAAI rows.

## Acceptance criteria

A Copart row with complete known signals must have `max_points_evaluable = 100` and a score on the 0–100 scale. A row with missing signals must expose reduced `coverage_percent` and `missing_fields` rather than silently presenting the score as fully supported. A Copart row with an explicit `Runs/Drives` value must show the normalized and raw values. An unclassified seller must be visible as a coverage limitation rather than being awarded a fabricated category. IAAI scores and policy version must remain unchanged.
