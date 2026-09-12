begin;

-- Preconditions: only the nine explicitly verified Copart seller names are eligible.
create temporary table seller_reclassification_targets (
    seller_name_key text primary key,
    category text not null,
    confidence numeric(6,5) not null,
    evidence text not null
) on commit drop;

insert into seller_reclassification_targets (seller_name_key, category, confidence, evidence)
values
    ('state farm insurance', 'insurance', 0.95000, 'ai_verified_external_sources'),
    ('usaa', 'insurance', 0.95000, 'ai_verified_external_sources'),
    ('geico', 'insurance', 0.95000, 'ai_verified_external_sources'),
    ('progressive', 'insurance', 0.95000, 'ai_verified_external_sources'),
    ('bristol west insurance', 'insurance', 0.95000, 'ai_verified_external_sources'),
    ('farmers insurance', 'insurance', 0.95000, 'ai_verified_external_sources'),
    ('farmers insurance company of flemington', 'insurance', 0.95000, 'ai_verified_external_sources'),
    ('csaa', 'insurance', 0.90000, 'ai_verified_external_sources'),
    ('aig insurance', 'insurance', 0.95000, 'ai_verified_external_sources');

-- Abort rather than silently applying to an unexpected population.
do $$
declare
    actual_count bigint;
begin
    select count(*) into actual_count
    from public.inventory_current_v2 v
    join seller_reclassification_targets t
      on lower(btrim(v.seller_name)) = t.seller_name_key
    where v.is_active
      and lower(btrim(v.platform)) = 'copart'
      and lower(coalesce(v.seller_type, '')) = 'unknown';
    if actual_count <> 648 then
        raise exception 'Seller reclassification precondition failed: expected 648 active Copart unknown rows, found %', actual_count;
    end if;
end $$;

update public.inventory_current_v2 v
set seller_type = t.category,
    seller_class = t.category,
    seller_is_insurance = true,
    seller_is_rental = false,
    seller_is_credit_company = false,
    seller_classification_confidence = t.confidence,
    seller_needs_review = false,
    seller_classification_evidence = t.evidence,
    seller_taxonomy_version = 'seller_taxonomy_ai_verified_v1_20260912',
    record_version = v.record_version + 1,
    updated_at = now()
from seller_reclassification_targets t
where v.is_active
  and lower(btrim(v.platform)) = 'copart'
  and lower(btrim(v.seller_name)) = t.seller_name_key
  and lower(coalesce(v.seller_type, '')) = 'unknown';

-- Postcondition inside the same transaction.
do $$
declare
    changed_count bigint;
begin
    select count(*) into changed_count
    from public.inventory_current_v2 v
    join seller_reclassification_targets t
      on lower(btrim(v.seller_name)) = t.seller_name_key
    where v.is_active
      and lower(btrim(v.platform)) = 'copart'
      and v.seller_type = 'insurance'
      and v.seller_class = 'insurance'
      and v.seller_classification_evidence = 'ai_verified_external_sources';
    if changed_count <> 648 then
        raise exception 'Seller reclassification postcondition failed: expected 648 verified rows, found %', changed_count;
    end if;
end $$;

commit;
