# Inventory V2 initial block loader

The initial rebuild path is intentionally incremental. It accepts one platform per run, a maximum of 1–1,000 lots, and a page cursor. The default mode is dry-run; V2 writes require the explicit `--write` flag and the production AuctionsAPI write gate.

Each run acquires a platform-specific lease, reads AuctionsAPI changed-lot pages, maps rows through the canonical mapper, applies the existing eligibility evaluator, and writes eligible rows through `IInventoryV2BatchWriter` (`COPY` + set-based `MERGE`). The loader does not call the legacy `PersistAsync` boundary and does not write V1.

A block records a sync run and page progress. The operator can resume with `--start-page` after reviewing the JSON result. The workflow rejects a maximum over 1,000, rejects invalid cursors, defaults to `DRY_RUN`, and creates a temporary Container Apps Job from the production job template. The temporary job is deleted on exit.

Operational command:

```text
dotnet Lsc.Inventory.Api.dll --auctionsapi-v2-initial-block --platform copart|iaai --maximum 1000 --start-page 1
```

To enable the first V2 write block explicitly:

```text
dotnet Lsc.Inventory.Api.dll --auctionsapi-v2-initial-block --platform copart|iaai --maximum 1000 --start-page 1 --write
```

The first production use must be a dry-run. After inspecting mapped/eligible/discarded/quarantined counts and nomenclator fields, the same block can be rerun with `LOAD_V2_WRITE`. The loader is not a reset operation; it must not be run until the reset window and destination-table state are separately authorized.
