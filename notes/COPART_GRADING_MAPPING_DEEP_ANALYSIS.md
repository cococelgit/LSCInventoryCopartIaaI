# Deep Analysis: Copart Excel Mapping and Grading Coverage

## Executive conclusion

The Copart grading v2 code is present in the production promotion path, so the low scores are not explained solely by an old image. The dominant issue is **signal coverage and semantic mapping**: the Excel contains useful fields, but several of them are either not mapped into the canonical contract, are mapped to a field the grader does not read, or are deliberately left empty. As a result, Copart loses points and coverage before the score is persisted.

The current pre-grade is a conservative **0–60 point model**, not a normalized 0–100 score. Its five factors are seller and traceability (15), declared mechanical condition (15), declared damage (15), title/documentation (10), and information quality (5). The current formula also applies explicit penalties for airbags, keys, secondary damage, and uncertainty. Photos, resale economics, market demand, repair cost, and estimated retail value are not currently used in the pre-grade formula.

## Deployment finding

The successful Copart promotion run was [33883429420](https://github.com/cococelgit/LSCInventoryCopartIaaI/actions/runs/33883429420), based on commit `51bc617`. The Copart v2 scoring commit is included in that history. Therefore the immediate operational diagnosis is: **the new policy is deployed, but the inputs feeding it are incomplete or semantically weak for Copart**.

The API deployment runs seen afterward are separate from the dedicated Copart job and must not be treated as evidence that the Copart job changed. The Copart-only promotion workflow remains the authoritative deployment path for `job-lsc-copart-excel-prod` and `job-lsc-copart-auto-prod`.

## What the Excel provides versus what the adapter maps

The test fixture and adapter support a broad Copart header set, including lot identity, VIN, year, make, model group/detail, title, damage, keys, runs/drives, odometer, seller, facility, auction timing, pricing, media, notes, body data, and the `Last Updated Time` watermark.

| Excel signal | Canonical destination | Persisted? | Used by current grading? | Assessment |
|---|---|---:|---:|---|
| `Lot number` | `AuctionVehicle.LotNumber` → `auction_lots.lot_number` | Yes | Indirectly | Correct identity anchor |
| `VIN` | `AuctionVehicle.Vin` → `auction_lots.vin` | Yes | No direct points | Correct, must remain validated |
| `Year` | `AuctionVehicle.Year` | Yes | No direct points | Correct |
| `Make` | `AuctionVehicle.Make` | Yes | No direct points | Correct; canonical aliases applied |
| `Model Detail` / `Model Group` | `AuctionVehicle.Model` | Yes | No direct points | Detail has precedence; good |
| `Trim` | `VehicleSpecs.Trim` | Payload/raw only | No | Not promoted to the primary vehicle projection or score |
| `Vehicle Type` / `Body Style` | `VehicleType`, `VehicleSpecs.BodyStyle` | Partially | No direct points | Good source preservation; body style backfill exists |
| `Damage Description` | `Condition.PrimaryDamage`, `Damage` | Yes | Yes, F03 | Critical grading input |
| `Secondary Damage` | `Condition.SecondaryDamage` | Yes | Yes, P03 | Critical penalty input |
| `Sale Title Type` | title mapper → `SaleDocument.Name`, `Title` | Yes | Yes, F04 | Mapping exists; taxonomy quality still matters |
| `Sale Title State` | `SaleDocument.State` and title notes | Yes/payload | Indirectly | Must remain separate from title type |
| `Has Keys-Yes or No` | `Condition.HasKey` | Yes | Yes, F05/P02 | Correct if values are exactly Yes/No/Y/N/True/False |
| `Runs/Drives` | `Condition.RunCondition.Raw/Normalized` | Yes | Yes, F02 | High-risk semantic path; requires regression verification |
| `Odometer` | `OdometerInfo.Miles` | Yes | Yes, F05 | Correct when numeric and positive |
| `Odometer Brand` | `OdometerInfo.Status` | Payload/projection | No direct points | Useful provenance, not used for quality scoring |
| `Seller Name` | `Seller.Name` | Yes | Indirectly | Seller type is left null at adapter construction |
| Seller category/type | `Seller.Type` | Derived later | Yes, F01 | Major risk; depends on taxonomy classification from name/raw evidence |
| `Location state/city/ZIP` | facility/location | Yes | No direct points | Correct |
| `Yard number/name` | facility/location | Yes | No direct points | Correct |
| `Sale Date`, time, timezone | `Auction.AuctionAt` | Yes | No direct points | Correct if timezone token is recognized |
| `Sale Status` | `Auction.State/LotStatus` | Yes | No direct points | Correct |
| `Sale Light` | `Auction.LotSubStatus` | Yes/payload | No direct points | Useful but not scored |
| `High Bid...` | `Pricing.CurrentBidUsd` | Yes | No direct points | Correct numeric parsing required |
| `Buy-It-Now Price` | `Pricing.BuyNowUsd` | Yes | No direct points | Positive-only rule is correct |
| `Est. Retail Value` | `Pricing.EstimatedRetailValueUsd` | Yes | No | Important unused signal |
| `Repair cost` | `Pricing.RepairCostUsd` | Yes | No | Important unused signal |
| `Special Note` / `Announcements` | notes | Yes/payload | No direct points | Potentially valuable condition evidence currently ignored |
| `Image Thumbnail` / `Image URL` | media | Yes/payload | No | Explicitly excluded from current pre-grade |
| `Engine`, `Cylinders`, `Fuel Type`, `Transmission`, `Drive` | vehicle specs | Yes/payload | No direct points | Useful but not scored |
| `Last Updated Time` | watermark metadata | Yes/raw | No | Correct incremental-processing input |

## Highest-impact grading gaps

### 1. Seller classification is fragile for Copart

The adapter creates `new AuctionSeller { Name = Get(row, "Seller Name"), Type = null }`. The canonical cleaner later classifies the seller using raw type, class, text class, and name. This can work only if the seller taxonomy recognizes the seller name reliably. If the Excel seller is generic, abbreviated, or inconsistent, the classification can become `UNCLASSIFIED` or low-confidence. Since F01 is worth 15 points, this is one of the largest sources of low Copart scores.

The audit must report, by seller name/category: coverage, taxonomy category, confidence, and review rate. We should not infer a seller type from a generic name without an explicit taxonomy rule; instead, preserve `seller_name_raw`, classify conservatively, and expose the evidence.

### 2. Run & Drive must be verified end-to-end

The adapter correctly reads the exact `Runs/Drives` column and stores raw plus normalized values. However, the grader reads the legacy-compatible `RunCondition.Value`/`Label` path, while the adapter writes `Normalized`/`Raw`. The contract currently aliases `Value` to the normalized backing value, so this may work after serialization/cleaning, but it is a high-risk integration point and must be covered by an end-to-end test that calls the actual grader after mapping and cleaning.

If this path fails, Copart loses all 15 points for F02 because the grader treats mechanical condition as unavailable. The production query should inspect `factor_scores` for F02 and the persisted payload for `run_condition` to confirm the value is `RUNS_AND_DRIVES`, `STARTS`, `STATIONARY`, or `UNVERIFIED`.

### 3. The Excel has useful condition evidence that the grader ignores

`Special Note`, `Announcements`, `Sale Light`, `Odometer Brand`, `Est. Retail Value`, and `Repair cost` are mapped or preserved but do not influence the current pre-grade. In particular, Copart announcements may contain meaningful condition signals such as flood, mechanical, key, starts, or title warnings. These must not be used through uncontrolled text inference; they should be normalized through a bounded dictionary with raw evidence and explicit tests.

### 4. Model/trim/body fields are not grading inputs

`Model Detail` and `Trim` are captured, but the current grading engine does not award points for identity completeness beyond the F05 information-quality factor. The trim is stored under `VehicleSpecs.Trim`, not as a first-class `auction_lots` column. That is acceptable for raw preservation, but it prevents simple SQL/UI verification and can make downstream matching appear incomplete.

The next implementation should retain the raw trim and expose a canonical `trim` projection in the vehicle payload without changing the IAAI contract. It should also preserve `Model Group` and `Model Detail` separately in raw evidence so that the chosen model can be audited.

### 5. The score scale is inherently conservative

The factor maximum is 60 points. A typical Copart row with a mapped seller category, Runs & Drives, salvage title, front damage, positive odometer, and keys can still land around the mid-40s before penalties. A missing seller category, missing run condition, uncertain damage, or missing keys can reduce it sharply. This is not necessarily a bad score; it is a pre-grade with explicit uncertainty, not a vehicle-quality guarantee.

The portal should display the score together with coverage and confidence, not present the raw pre-grade as if it were a 100-point quality score. Any future 0–100 display conversion must be a presentation transform, not a silent change to the persisted policy.

## What must be measured from the real Excel/database

The repository contains the field-coverage auditor, but the actual production Excel is not present in the repository. Therefore exact percentages cannot responsibly be invented from source code alone. The next diagnostic run should calculate, for all Copart rows and for the scored subset:

| Diagnostic | Why it matters |
|---|---|
| Non-empty coverage by raw Excel header | Finds source sparsity versus mapping loss |
| `Seller.Name` versus `Seller.Type` and confidence | Quantifies F01 loss |
| `Runs/Drives` raw and normalized values | Confirms F02 input integrity |
| Primary and secondary damage coverage | Quantifies F03 and P03 effects |
| Keys coverage and value distribution | Quantifies F05/P02 effects |
| Title code → normalized category coverage | Confirms F04 consistency |
| Odometer positive coverage | Quantifies F05 contribution |
| Trim/body/engine/fuel/transmission coverage | Identifies identity/specification gaps |
| Score distribution by `coverage_percent`, `status`, and policy version | Separates formula behavior from missing data |
| Factor and penalty distributions | Identifies the dominant point losses |
| Score timestamp versus latest Copart update | Detects stale scores after mapping changes |

## Recommended correction sequence

The correction should be staged and Copart-only.

First, add a production-safe field audit and a fixture that runs the complete path: Excel row → adapter → canonical cleaner → eligibility → scorer. The fixture must assert the actual factor values, not only the mapped object.

Second, correct the highest-impact mappings: seller taxonomy evidence, Run & Drive end-to-end propagation, exact damage normalization, keys normalization, and title-code normalization. Preserve every raw source value and add normalized values to `AdditionalData` for auditability.

Third, add bounded normalization for announcements and special notes only where an explicit rule exists. Do not infer that a vehicle runs, has keys, or is mechanically sound from an ambiguous sentence.

Fourth, expose `trim` and the selected model source in the canonical payload, and add SQL/audit coverage for engine, cylinders, fuel, transmission, body style, retail value, and repair cost. These fields may support a future grading revision, but they should not silently alter the current score until the scoring policy is approved.

Finally, recalculate active Copart scores using the existing `scoring_backfill` path after the mapping release. Do not modify IAAI, Apibara, the API image, cron configuration, or any unrelated job.

## Current decision

The evidence supports a **mapping and coverage remediation**, not an immediate increase to the numeric score formula. Raising scores without fixing the inputs would create false confidence and would make Copart incomparable with IAAI in an uncontrolled way. The first release should make the existing v2 score accurate and explainable; a separate policy change can later decide whether to add bounded Copart-specific signals or normalize the display to 100.

## v3 implementation now in the working tree

The Copart-only v3 patch changes the policy identifier to `lsc_pre_grade_v3` and uses an explicit 100-point weighting: seller and traceability 20, mechanical condition 25, damage 25, title/documentation 15, and information quality 15. The existing IAAI `lsc_pre_grade_v1` path remains unchanged.

The adapter now classifies the Copart seller name before scoring and persists the category, taxonomy version, confidence, and evidence. It also preserves raw evidence for seller, primary/secondary damage, Run & Drive, keys, and odometer. Numeric parsing accepts common comma-separated and unit-suffixed values, while key parsing accepts explicit variants such as `NO KEYS` and `WITHOUT KEY` without inferring a key from unrelated text.

The tests cover the v3 0–100 normalization, the seller-name-to-taxonomy path, Copart processor and backfill policy versioning, Run & Drive preservation, and existing IAAI behavior. The current suite passes 162 tests.

This patch has **not** been deployed and no backfill has been run. Production deployment must remain Copart-only and should be followed by a controlled active-Copart backfill after the persisted factor distributions are verified.
