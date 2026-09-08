# Inventory search latency — optimization candidate

**Author:** Manus AI  
**Date:** 2026-09-08  
**Safety status:** isolated branch; no production deployment, schema change, data write, job change, or portal change.

## Executive conclusion

The exact portal browse request currently has a measured **p50 of 11.688 seconds and p95 of 12.465 seconds** over six consecutive production reads. The response contained 117,141 eligible vehicles. Reducing the requested page from 24 vehicles to one vehicle previously left the request near 10.6 seconds, so response serialization is not the primary bottleneck.

The deployed API executes an exact count and the item query sequentially. The item query joins `inventory_vehicle_score_current` to `inventory_search_current`, orders the full eligible set by `score.pre_grade` and then applies the requested secondary order before `LIMIT 24`. This cross-table ordering prevents the search projection from carrying an index that matches the actual browse order.

> **Decision:** The candidate is ready for Staging validation, but it is not approved for Production. The target of p95 ≤ 2 seconds must be demonstrated against a Staging copy of representative data before promotion.

## Production baseline

| Metric | Result |
|---|---:|
| Samples | 6 |
| Minimum | 11.240 s |
| p50 | 11.688 s |
| p95 | 12.465 s |
| Maximum | 13.830 s |
| Mean | 12.060 s |
| Eligible vehicles in last response | 117,141 |

The measured request used the same effective portal contract: current-day auction floor, `excludeSpecialTitles=true`, page 1, 24 vehicles and `updated-desc`. The raw measurements are preserved in `artifacts/search-performance/baseline-2026-09-08.tsv`.

## Candidate implementation

| Change | Purpose | Default |
|---|---|---|
| Denormalized score summary in `inventory_search_current` | Remove the scoring join from browse and filtered counts | Disabled |
| Partial score/order indexes | Serve grade-first + recently-updated ordering directly from the search projection | Not applied |
| Visible auction-date index | Support the portal's mandatory current-day date floor | Not applied |
| Parallel count and item reads | Replace sequential latency with the slower of the two reads | Disabled |
| Capability gate | Fall back to the legacy query unless the column and required index exist | Always enforced |
| Transactional score synchronization | Keep the denormalized score aligned with `inventory_vehicle_score_current` | Runs only when the migration and flag are active |

The implementation preserves the public response contract, exact total, page size, grading-first order, secondary sort, filter semantics and score payload. The legacy path remains available and is still the default.

## Validation completed outside Production

| Gate | Result |
|---|---:|
| Full .NET suite | 233/233 passed |
| Release build | 0 errors; 0 warnings |
| Migration applied twice to disposable local PostgreSQL | Passed |
| Legacy vs denormalized ordering differences | 0 |
| Score projection mismatches | 0 |
| Invalid/not-ready indexes | 0 |
| Rollback remaining score columns | 0 |
| Rollback remaining v7 indexes | 0 |

The local verification is deterministic and proves migration idempotency, SQL validity, result ordering equivalence and rollback. It does **not** represent production-scale performance and is not used to claim the two-second target.

## Required Staging gate

The next step must be executed on an isolated Staging database containing a representative copy of the search and scoring tables.

1. Deploy the candidate image with both flags disabled.
2. Apply `016_search_projection_denormalized_score_v1.sql` to Staging only.
3. Run `016_search_projection_denormalized_score_v1_verify.sql`; every mismatch must be zero and all indexes must be valid and ready.
4. Enable only `UseDenormalizedScoringSearch` and restart the Staging API.
5. Compare exact totals, ordered lot fingerprints and vehicle score payloads for the default browse and representative seller, platform, state, year, Buy Now and Run & Drive filters.
6. Run 20 samples with `tools/benchmark-inventory-search.sh`.
7. Enable `RunBrowseCountAndItemsInParallel`, restart Staging and repeat the same comparisons and benchmark.
8. Accept only if browse p95 is ≤ 2.0 seconds, errors remain zero, all result fingerprints match the legacy path, and PostgreSQL CPU/IO stays within the agreed operating envelope.
9. If any gate fails, disable both flags. If necessary, run the explicit rollback script after traffic is removed from the candidate revision.

## Repository integrity warning

The deployed production image references commit `5238e8538bd7f906d0ce48f7f1a7d2480b696600`, while the current `main` branch has unrelated history and is not a descendant of that commit. The optimization branch therefore starts from the exact deployed SHA. Promotion must merge intentionally; replacing Production from current `main` without reconciliation risks losing active production fixes.

## References

[1]: https://ca-lsc-inventory-api-prod.lemoncliff-62ee11e1.eastus2.azurecontainerapps.io "LSC Inventory production API"
[2]: https://github.com/cococelgit/LSCInventoryCopartIaaI/commit/5238e8538bd7f906d0ce48f7f1a7d2480b696600 "Exact commit referenced by the deployed production image"
