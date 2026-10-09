begin;
set local statement_timeout = '45s';
set local lock_timeout = '5s';
create table if not exists inventory_scoring_queue_repair_audit (
 repair_id text not null,
 lot_key text not null,
 before_row jsonb not null,
 reason text not null,
 captured_at timestamptz not null default now(),
 primary key (repair_id, lot_key)
);
create temporary table scoring_repair_candidates on commit drop as
select q.lot_key, to_jsonb(q) as before_row,
 case when c.lot_key is null or not c.is_active then 'obsolete-inactive-inventory'
 when s.input_hash=c.score_input_hash and s.policy_version=q.policy_version then 'obsolete-already-scored-current-input'
 when q.input_hash is distinct from c.score_input_hash then 'refresh-current-inventory-input'
 else null end as reason,
 c.score_input_hash as current_hash, c.last_seen_at as current_observed_at
from inventory_vehicle_scoring_queue q
left join inventory_current_v2 c on c.lot_key=q.lot_key
left join inventory_vehicle_score_current s on s.lot_key=q.lot_key
where q.status in ('queued','failed') and (
 c.lot_key is null or not c.is_active or
 (s.input_hash=c.score_input_hash and s.policy_version=q.policy_version) or
 q.input_hash is distinct from c.score_input_hash
)
for update of q skip locked;
insert into inventory_scoring_queue_repair_audit(repair_id,lot_key,before_row,reason)
select '2026-10-09-scoring-host-repair',lot_key,before_row,reason from scoring_repair_candidates
on conflict do nothing;
with changed as (
 update inventory_vehicle_scoring_queue q
 set status=case when r.reason='refresh-current-inventory-input' then 'queued' else 'skipped' end,
 input_hash=case when r.reason='refresh-current-inventory-input' then r.current_hash else q.input_hash end,
 source_observed_at=case when r.reason='refresh-current-inventory-input' then r.current_observed_at else q.source_observed_at end,
 source_run_id=case when r.reason='refresh-current-inventory-input' then null else q.source_run_id end,
 attempts=case when r.reason='refresh-current-inventory-input' then 0 else q.attempts end,
 last_error=case when r.reason='refresh-current-inventory-input' then null else r.reason end,
 claimed_at=null,
 completed_at=case when r.reason='refresh-current-inventory-input' then null else now() end,
 updated_at=now()
 from scoring_repair_candidates r where r.lot_key=q.lot_key
 returning r.reason
)
select json_build_object('section','reconciled','reason',reason,'rows',count(*))::text from changed group by reason;
select json_build_object('section','queue_after','status',status,'rows',count(*))::text from inventory_vehicle_scoring_queue where status in ('queued','processing','failed') group by status;
commit;
