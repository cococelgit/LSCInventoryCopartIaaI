#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
database="lsc_search_perf_validation"

cleanup() {
  sudo -u postgres dropdb --if-exists "$database" >/dev/null 2>&1 || true
}
trap cleanup EXIT

cleanup
sudo -u postgres createdb "$database"

sudo -u postgres psql -v ON_ERROR_STOP=1 -d "$database" <<'SQL'
create table schema_migrations (
    migration_id text primary key,
    applied_at timestamptz not null default now()
);

create table inventory_search_current (
    lot_key text primary key,
    platform text not null,
    is_active boolean not null default true,
    is_special_title boolean not null default false,
    observed_at timestamptz not null,
    auction_at timestamptz
);

create table inventory_vehicle_score_current (
    lot_key text primary key,
    status text not null,
    pre_grade numeric,
    buy_score numeric,
    max_points_evaluable numeric not null,
    coverage_percent numeric not null,
    confidence_percent numeric not null,
    category text,
    policy_version text not null,
    source_observed_at timestamptz not null,
    scored_at timestamptz not null
);

insert into inventory_search_current
    (lot_key, platform, is_active, is_special_title, observed_at, auction_at)
values
    ('copart:100', 'copart', true, false, '2026-09-08 12:00:00+00', '2026-09-10 12:00:00+00'),
    ('iaai:200', 'iaai', true, false, '2026-09-08 11:00:00+00', '2026-09-11 12:00:00+00'),
    ('copart:300', 'copart', true, false, '2026-09-08 10:00:00+00', '2026-09-12 12:00:00+00'),
    ('iaai:400', 'iaai', true, false, '2026-09-08 09:00:00+00', '2026-09-13 12:00:00+00'),
    ('copart:500', 'copart', true, true, '2026-09-08 13:00:00+00', '2026-09-14 12:00:00+00'),
    ('iaai:600', 'iaai', false, false, '2026-09-08 14:00:00+00', '2026-09-15 12:00:00+00');

insert into inventory_vehicle_score_current
    (lot_key, status, pre_grade, buy_score, max_points_evaluable, coverage_percent,
     confidence_percent, category, policy_version, source_observed_at, scored_at)
values
    ('copart:100', 'complete', 54, null, 60, 90, 90, 'A', 'v1', '2026-09-08 12:00:00+00', '2026-09-08 12:01:00+00'),
    ('iaai:200', 'complete', 54, null, 60, 90, 90, 'A', 'v1', '2026-09-08 11:00:00+00', '2026-09-08 11:01:00+00'),
    ('copart:300', 'complete', 42, null, 60, 80, 80, 'B', 'v1', '2026-09-08 10:00:00+00', '2026-09-08 10:01:00+00'),
    ('copart:500', 'complete', 59, null, 60, 95, 95, 'A', 'v1', '2026-09-08 13:00:00+00', '2026-09-08 13:01:00+00');
SQL

sudo -u postgres psql -v ON_ERROR_STOP=1 -d "$database" >/dev/null < "$repo_root/infra/sql/016_search_projection_denormalized_score_v1.sql"
sudo -u postgres psql -v ON_ERROR_STOP=1 -d "$database" >/dev/null < "$repo_root/infra/sql/016_search_projection_denormalized_score_v1.sql"

result="$(sudo -u postgres psql -v ON_ERROR_STOP=1 -At -d "$database" <<'SQL'
with legacy as (
    select latest.lot_key,
           row_number() over (order by score.pre_grade desc nulls last, latest.observed_at desc nulls last, latest.lot_key) as position
    from inventory_search_current latest
    left join inventory_vehicle_score_current score on score.lot_key = latest.lot_key
    where latest.is_active and not latest.is_special_title and latest.auction_at >= '2026-09-08 04:00:00+00'
), optimized as (
    select latest.lot_key,
           row_number() over (order by latest.score_pre_grade desc nulls last, latest.observed_at desc nulls last, latest.lot_key) as position
    from inventory_search_current latest
    where latest.is_active and not latest.is_special_title and latest.auction_at >= '2026-09-08 04:00:00+00'
), differences as (
    (select * from legacy except select * from optimized)
    union all
    (select * from optimized except select * from legacy)
), projection_mismatches as (
    select latest.lot_key
    from inventory_search_current latest
    join inventory_vehicle_score_current score on score.lot_key = latest.lot_key
    where latest.score_pre_grade is distinct from score.pre_grade
       or latest.score_status is distinct from score.status
       or latest.score_source_observed_at is distinct from score.source_observed_at
), invalid_indexes as (
    select indexrelid
    from pg_index
    where indexrelid in (
        'ix_inventory_search_visible_score_observed_v7'::regclass,
        'ix_inventory_search_active_score_observed_v7'::regclass,
        'ix_inventory_search_visible_platform_score_observed_v7'::regclass,
        'ix_inventory_search_visible_auction_v7'::regclass)
      and (not indisvalid or not indisready)
)
select json_build_object(
    'orderingDifferences', (select count(*) from differences),
    'projectionMismatches', (select count(*) from projection_mismatches),
    'invalidIndexes', (select count(*) from invalid_indexes),
    'migrationRows', (select count(*) from schema_migrations where migration_id = '016_search_projection_denormalized_score_v1'));
SQL
)"

echo "$result"
test "$result" = '{"orderingDifferences" : 0, "projectionMismatches" : 0, "invalidIndexes" : 0, "migrationRows" : 1}'

sudo -u postgres psql -v ON_ERROR_STOP=1 -d "$database" >/dev/null < "$repo_root/infra/sql/016_search_projection_denormalized_score_v1_rollback.sql"

rollback_result="$(sudo -u postgres psql -v ON_ERROR_STOP=1 -At -d "$database" <<'SQL'
select json_build_object(
    'remainingScoreColumns', (
        select count(*)
        from information_schema.columns
        where table_schema = current_schema()
          and table_name = 'inventory_search_current'
          and column_name like 'score_%'),
    'remainingMigrationRows', (
        select count(*)
        from schema_migrations
        where migration_id = '016_search_projection_denormalized_score_v1'),
    'remainingIndexes', (
        select count(*)
        from pg_class
        where relname like 'ix_inventory_search_%_v7'));
SQL
)"

echo "$rollback_result"
test "$rollback_result" = '{"remainingScoreColumns" : 0, "remainingMigrationRows" : 0, "remainingIndexes" : 0}'
