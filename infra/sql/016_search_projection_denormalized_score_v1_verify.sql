-- Read-only staging verification. All mismatch counts must be zero and all indexes must be valid.

select count(*) as score_projection_mismatches
from inventory_search_current latest
join inventory_vehicle_score_current score on score.lot_key = latest.lot_key
where latest.score_status is distinct from score.status
   or latest.score_pre_grade is distinct from score.pre_grade
   or latest.score_buy_score is distinct from score.buy_score
   or latest.score_max_points_evaluable is distinct from score.max_points_evaluable
   or latest.score_coverage_percent is distinct from score.coverage_percent
   or latest.score_confidence_percent is distinct from score.confidence_percent
   or latest.score_category is distinct from score.category
   or latest.score_policy_version is distinct from score.policy_version
   or latest.score_scored_at is distinct from score.scored_at
   or latest.score_source_observed_at is distinct from score.source_observed_at;

select indexrelid::regclass::text as index_name, indisvalid, indisready
from pg_index
where indexrelid in (
    'ix_inventory_search_visible_score_observed_v7'::regclass,
    'ix_inventory_search_active_score_observed_v7'::regclass,
    'ix_inventory_search_visible_platform_score_observed_v7'::regclass,
    'ix_inventory_search_visible_auction_v7'::regclass)
order by index_name;
