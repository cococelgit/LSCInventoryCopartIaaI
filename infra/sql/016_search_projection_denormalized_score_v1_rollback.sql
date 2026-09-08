-- Disable Persistence:UseDenormalizedScoringSearch before running this rollback.
-- DROP INDEX CONCURRENTLY requires this script to run outside an explicit transaction.

drop index concurrently if exists ix_inventory_search_visible_score_observed_v7;
drop index concurrently if exists ix_inventory_search_active_score_observed_v7;
drop index concurrently if exists ix_inventory_search_visible_platform_score_observed_v7;
drop index concurrently if exists ix_inventory_search_visible_auction_v7;

alter table inventory_search_current drop column if exists score_source_observed_at;
alter table inventory_search_current drop column if exists score_scored_at;
alter table inventory_search_current drop column if exists score_policy_version;
alter table inventory_search_current drop column if exists score_category;
alter table inventory_search_current drop column if exists score_confidence_percent;
alter table inventory_search_current drop column if exists score_coverage_percent;
alter table inventory_search_current drop column if exists score_max_points_evaluable;
alter table inventory_search_current drop column if exists score_buy_score;
alter table inventory_search_current drop column if exists score_pre_grade;
alter table inventory_search_current drop column if exists score_status;

delete from schema_migrations
where migration_id = '016_search_projection_denormalized_score_v1';
