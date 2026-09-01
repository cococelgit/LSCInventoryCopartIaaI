# Runbook — R24 Title Mapping (Copart Only)

**Scope.** R24 deploys a new image to `ca-lsc-inventory-api-prod` only. It does not create, update, schedule, or run any Container App Job. It preserves `Sync__Enabled=false`, `Persistence__RunMigrations=false`, the API identity, secrets, ingress, scale configuration, IAAI schedule, Copart Excel configuration, Copart automatic arguments and the legacy generic job.

## Pre-flight contract

The deployment script refuses to update the API unless the API image is exactly r22, Single revision mode is enabled, IAAI remains on r14 at `15,45 * * * *`, Copart automatic retains exactly `--copart-excel-run`, and the legacy generic job is Manual. It compares configuration fingerprints before and after the API update. A mismatch exits before changing the API, or reports the unexpected changed resource after the update.

| Artifact | Immutable reference |
|---|---|
| API source context | `/manus-storage/lsc-inventory-engine-api-r24-title-mapping.tar_4377cc70.gz` |
| Source SHA-256 | `e796f2c2e38d1b3557e1b016db9701b13dc1fc32af2debb012785c43b5526ef3` |
| Deployment script | `/manus-storage/federated-deploy-api-title-mapping-r24_410eca7a.sh` |
| Script SHA-256 | `b6c953b664e87a4ef835962a61ab7e687da3437bbf1365cdf58cd67910196c51` |
| Image created | `acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:inventory-api-integrated-r24-copart-title-mapping` |

## Azure operation

1. Use the prepared ARM template URL in the Azure Portal while authenticated to subscription `LSC Inventory Feed Project` and resource group `rg-lsc-inventory-prod`.
2. Review the template. The only Azure resource it creates is a temporary `Microsoft.Resources/deploymentScripts` runner using the existing user-assigned identity. That runner builds the image and updates only `ca-lsc-inventory-api-prod`.
3. Start the deployment. Do **not** press **Run now** on IAAI, Copart Excel, Copart automatic, generic ingestion, or scoring jobs.
4. Wait for the deployment to report `API_R24_DEPLOY_COMPLETED`. It must also report every `*_JOB_CHANGED=false`, `COPART_APIBARA_ENABLED=false`, `MIGRATIONS_ENABLED=false` and `PROJECTION_REBUILD_STARTED=false`.

## Controlled projection rebuild

### R24 diagnostic

R24 passed `healthz` and exposed the expected per-vehicle mapping for Copart `CT` (`titleType=CT`, `titleCategory=CLEAN`, `titleDisplayLabel=Clean Title · Theft Recovery`). However, the first synchronous rebuild request was closed by the ingress before the response returned. A post-attempt read showed the API healthy and the existing projection still ready with the previous generation, so the operation did not partially commit and did not alter jobs or source data.

### R25 durable request handling

R25 changes only the API request handling. It starts the existing transactional rebuild server-side, immediately returns `202 Accepted`, prevents duplicate starts with an in-process coordinator, and appends observable `rebuild` state to the protected status endpoint. It does not add a job, schedule, table, migration, source call, or schema change. The existing PostgreSQL advisory lock remains the authority across instances.

| Artifact | Immutable reference |
|---|---|
| API source context | `/manus-storage/lsc-inventory-engine-api-r25-rebuild-async.tar_dc2fa24a.gz` |
| Source SHA-256 | `ab2abf7f1857a847b40f0bba87fccc0f81976a8c41805b43f278e257ee093daa` |
| Deployment script | `/manus-storage/federated-deploy-api-rebuild-async-r25_a97ebacb.sh` |
| Script SHA-256 | `14204c2035263ff362bdf9de9b5a43bf64ed2e2120ae616c248a206de1bd81b6` |
| ARM template | `/manus-storage/azure-template-api-rebuild-async-r25_54a028da.json` |
| Image created | `acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:inventory-api-integrated-r25-rebuild-async` |

Deploy r25 as a separate **API-only** operation only after confirming its script guardrails. It expects r24 as the current image and verifies exactly the same API configuration and IAAI, Copart Excel, Copart automatic, and legacy generic job fingerprints before and after. It prints `PROJECTION_REBUILD_STARTED=false` because the deployment itself never calls the rebuild endpoint.

### Controlled projection rebuild after r25

Only after R25 passes `healthz`, call the authenticated internal endpoint:

```text
POST /internal/search-projection/rebuild
Authorization: Bearer <existing INVENTORY_API_TOKEN>
```

The response must be `202 Accepted`; it reports `accepted=true` and `status.isRunning=true`. Poll `GET /internal/search-projection/status` with the same token until `rebuild.isRunning=false`. Success requires `rebuild.lastError=null`, `rebuild.lastSuccessfulResult.ready=true`, and an updated projection generation. The endpoint takes a PostgreSQL transaction advisory lock, marks the projection unavailable while rebuilding, reuses the code-backed Copart dictionary and text-based IAAI rules, refreshes facets, and commits only when complete. The portal falls back to the existing source when the projection is not ready. Never run a manual SQL `UPDATE` or an `ALTER TABLE` for this release.

## Acceptance checks

After the rebuild succeeds, verify with authenticated read-only calls:

| Check | Expected result |
|---|---|
| `/healthz` | HTTP 200. |
| `/internal/search-projection/status` | Ready, with a populated row count and refresh timestamp. |
| `summary` with `platform=copart` | Title facet contains six categories, not codes such as `CT` or `SC`. |
| Search `platform=copart&titles=CLEAN` | Includes formerly code `CT` vehicles. |
| Search `platform=copart&titles=SPECIAL` | Includes special lots only on explicit selection; default excludes them. |
| Copart detail for a `CT` lot | `titleType` remains `CT`; `titleCategory=CLEAN`; label identifies Clean Title / Theft Recovery. |
| IAAI detail | Its original `SaleDocument.Name` remains intact and code dictionary is not applied. |

If any acceptance check fails, stop before changing jobs or data manually. Capture the endpoint response and revision name for review.
