# AuctionsAPI provider contract

AuctionsAPI is an interchangeable input provider, not a second inventory-processing pipeline.

## Provider responsibilities

The adapter may only issue the provider request, control the incremental window and pagination, select the requested auction domain, map the response into `AuctionVehicle`, and preserve the original row in `AuctionVehicle.RawSource`. It may not decide whether a vehicle is loaded, rejected, quarantined, scored, published, or deactivated.

## Canonical responsibilities

Every provider vehicle passes through `ICanonicalInventoryIngestionPipeline`. That boundary owns normalization, canonical cleaning, eligibility rules, title and seller evidence handling, persistence, lifecycle state, execution events, and the existing scoring path associated with persistence. The `buy_now_usd > 0` guardrail remains part of the canonical rules and is not reimplemented by a provider.

## Incremental semantics

`cars?minutes=N` supplies changed rows and `archived-lots?minutes=N` supplies explicit archive signals. The adapter uses the provider window with an overlap and follows provider pagination metadata. A partial window never calls full-source reconciliation. Archived lot keys use the canonical `{platform}:{lot}` identity and are passed to the store's explicit archived-lot deactivation method.

## Safety gates

The feature is disabled unless `AuctionsApi:Enabled=true` and a non-empty API key are configured. Canonical writes require the independent `AuctionsApi:AllowWrites=true` gate. The internal route defaults to `persist=false`; a valid token alone cannot activate writes. Existing Apibara, Copart Excel, Jobs, cron, identities, and production traffic remain unchanged until an explicit operational activation.

## Verification

The API branch `manus/api-r71-multi-provider` contains the implementation. The latest commit is `bd3beb7`. The suite passes with 183 tests and the Release build completes without warnings.
