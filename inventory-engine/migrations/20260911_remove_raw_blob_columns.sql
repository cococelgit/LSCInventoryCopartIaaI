BEGIN;

-- PostgreSQL is now the operational source of truth. Raw payloads and
-- eligibility decisions are already persisted in JSONB columns.
ALTER TABLE IF EXISTS auction_lot_versions
    DROP COLUMN IF EXISTS raw_blob_name;

ALTER TABLE IF EXISTS eligibility_decisions
    DROP COLUMN IF EXISTS audit_blob_name;

COMMIT;
