# Copart Media Deep Analysis and Fix

## Root causes found

1. The Excel column `Image URL` is a Copart image-catalog endpoint, not a photo. The adapter was putting it into `media.thumbs`, inflating the photo count and preventing lots from entering the media backfill.
2. Candidate selection relied on `auction_lots.media_photos_count <= 1`, so a lot with one real thumbnail plus the catalog URL could be incorrectly considered complete.
3. The resolver required a narrow set of exact JSON property names for thumbnail/HD flags and did not robustly select the best candidate when a catalog returned multiple variants.
4. Media enrichment inserts a new `auction_lot_versions` row with the same `observed_at` as the source lot. Readers that ordered only by `observed_at desc` could return the old version and hide the enriched gallery.
5. The production build uses `inventory-engine/Dockerfile`, so all changes were made under `inventory-engine/` only.

## Implemented changes

- `CopartExcelSnapshotAdapter` now stores only the actual `Image Thumbnail` as initial media and keeps `Image URL` exclusively in `_raw_source` for catalog resolution.
- `GetCopartMediaCandidatesAsync` counts real photo URLs after excluding `_raw_source.Image URL`, rather than trusting the inflated relational count.
- `CopartMediaResolver` now accepts case variants and aliases (`url`, `imageUrl`, `href`, `isThumbnail`, `isThumb`, `isHD`, etc.), validates HTTPS Copart hosts, selects the best URL per sequence, deduplicates URLs, preserves sequence order, and recognizes width/URL HD evidence.
- Public/latest readers now order same-timestamp versions by `id desc` as a deterministic tie-breaker: `GetPublicMediaManifestAsync`, `GetRecentAsync`, and `GetPageAsync`.
- No scoring formula, weights, IAAI flow, API image, cron, or secrets were changed.

## Validation

- Copart-focused tests: passed.
- Full `.NET` suite: **169 passed, 0 failed**.
- No production deploy or media backfill was executed.

## Deploy/backfill acceptance criteria

Before a production run, verify that the job uses the new image and run `media_enrich` in a controlled sample. Report candidates, resolved galleries, HD image count, thumbnail-only galleries, 404s, invalid URLs, and failures. Then query `auction_lots.media_photos_count` and the latest `auction_lot_versions.payload->media->thumbs` for the same lot keys. Do not start a mass backfill until the sample confirms that catalog URLs are absent from `media.thumbs`, enriched versions are selected, and the portal's read path returns the enriched gallery.
