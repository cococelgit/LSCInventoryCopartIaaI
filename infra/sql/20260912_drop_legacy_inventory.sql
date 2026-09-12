-- Destructive migration: legacy vehicle inventory only.
-- Run as the PostgreSQL table owner/admin after the V2-only application build is deployed.
-- Intentionally does not use CASCADE: dependency errors must stop the migration safely.
BEGIN;

DROP TABLE IF EXISTS public.inventory_search_current;
DROP TABLE IF EXISTS public.auction_lot_versions;
DROP TABLE IF EXISTS public.auction_lots;

COMMIT;
