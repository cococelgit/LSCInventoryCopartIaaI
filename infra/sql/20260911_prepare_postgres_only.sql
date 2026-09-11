begin;

alter table if exists auction_lot_versions
    alter column raw_blob_name drop not null;

alter table if exists eligibility_decisions
    alter column audit_blob_name drop not null;

commit;
