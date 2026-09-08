-- Search performance migration v1.
-- Apply only after validating the feature-flagged application build in Staging.
-- CREATE INDEX CONCURRENTLY requires this script to run outside an explicit transaction.

alter table inventory_search_current add column if not exists score_status text;
alter table inventory_search_current add column if not exists score_pre_grade numeric;
alter table inventory_search_current add column if not exists score_buy_score numeric;
alter table inventory_search_current add column if not exists score_max_points_evaluable numeric;
alter table inventory_search_current add column if not exists score_coverage_percent numeric;
alter table inventory_search_current add column if not exists score_confidence_percent numeric;
alter table inventory_search_current add column if not exists score_category text;
alter table inventory_search_current add column if not exists score_policy_version text;
alter table inventory_search_current add column if not exists score_scored_at timestamptz;
alter table inventory_search_current add column if not exists score_source_observed_at timestamptz;

update inventory_search_current latest
set score_status = score.status,
    score_pre_grade = score.pre_grade,
    score_buy_score = score.buy_score,
    score_max_points_evaluable = score.max_points_evaluable,
    score_coverage_percent = score.coverage_percent,
    score_confidence_percent = score.confidence_percent,
    score_category = score.category,
    score_policy_version = score.policy_version,
    score_scored_at = score.scored_at,
    score_source_observed_at = score.source_observed_at
from inventory_vehicle_score_current score
where score.lot_key = latest.lot_key;

create index concurrently if not exists ix_inventory_search_visible_score_observed_v7
    on inventory_search_current (score_pre_grade desc nulls last, observed_at desc nulls last, lot_key)
    where is_active and not is_special_title;

create index concurrently if not exists ix_inventory_search_active_score_observed_v7
    on inventory_search_current (score_pre_grade desc nulls last, observed_at desc nulls last, lot_key)
    where is_active;

create index concurrently if not exists ix_inventory_search_visible_platform_score_observed_v7
    on inventory_search_current (platform, score_pre_grade desc nulls last, observed_at desc nulls last, lot_key)
    where is_active and not is_special_title;

create index concurrently if not exists ix_inventory_search_visible_auction_v7
    on inventory_search_current (auction_at, lot_key)
    where is_active and not is_special_title;

analyze inventory_search_current;

insert into schema_migrations (migration_id)
values ('016_search_projection_denormalized_score_v1')
on conflict (migration_id) do nothing;
