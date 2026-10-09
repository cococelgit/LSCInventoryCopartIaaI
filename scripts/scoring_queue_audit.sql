begin transaction read only;
set local statement_timeout = '45s';
select json_build_object('section','queue_state','status',q.status,'rows',count(*),'inventoryMissing',count(*) filter (where c.lot_key is null),'inactive',count(*) filter (where not coalesce(c.is_active,false)),'hashMismatch',count(*) filter (where c.score_input_hash is distinct from q.input_hash),'alreadyCurrentScore',count(*) filter (where s.input_hash=c.score_input_hash and s.policy_version=q.policy_version),'activeHashMatching',count(*) filter (where c.is_active and c.score_input_hash=q.input_hash))::text
from inventory_vehicle_scoring_queue q left join inventory_current_v2 c on c.lot_key=q.lot_key left join inventory_vehicle_score_current s on s.lot_key=q.lot_key
where q.status in ('queued','processing','failed') group by q.status;
select json_build_object('section','failure_reason','lastError',last_error,'rows',count(*))::text from inventory_vehicle_scoring_queue where status='failed' group by last_error order by count(*) desc limit 5;
select json_build_object('section','failed_example','lot',q.lot_key,'status',q.status,'attempts',q.attempts,'isActive',c.is_active,'queueHash',q.input_hash,'currentHash',c.score_input_hash,'scoreHash',s.input_hash,'lastSeen',c.last_seen_at,'queueUpdated',q.updated_at,'error',q.last_error)::text from inventory_vehicle_scoring_queue q left join inventory_current_v2 c on c.lot_key=q.lot_key left join inventory_vehicle_score_current s on s.lot_key=q.lot_key where q.status='failed' order by q.updated_at desc limit 8;
commit;
