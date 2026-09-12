# Inventory V2 Empty Search Findings — 2026-09-12

After the first Copart V2 write block (run 34701230361), the loader reported Persisted=true for 1,000 observed rows, 876 eligible, 123 discarded, 1 quarantined, 0 failures. The portal search returned no vehicles.

The active V2 reader guard in `src/Lsc.Inventory.Api/Storage/PostgresSnapshotStore.InventoryV2Readers.cs` was:

```sql
select reader_enabled and writer_enabled
from inventory_v2_schema_state
where schema_name = 'inventory-current-v2'
  and schema_version >= @schema_version;
```

The reset intentionally left both flags disabled. This means the reader rejects search/detail/facets/recent while the writer may have run. The guard must depend only on `reader_enabled`; reader and writer lifecycle flags must be independent. The next verification must query actual V2 row counts and then enable `reader_enabled=true` while keeping `writer_enabled=false` before resuming ingestion.

The write-block workflow initially failed before the job because its jq expression escaped quotes incorrectly in `jq '. + [\"--write\"]'`; this was fixed in commit `eea497d7b3e04272e328073182d18b33defab0d5`. The rerun completed successfully as run `34701230361`.

Official AuctionsAPI date filter used: `sale_date_from=2026-09-12T04:00:00Z` for start of 2026-09-12 in Florida/EDT. No external source beyond the official documentation URL `https://auctionsapi.com/auction-docs` is used here.
