begin;

create table if not exists schema_migrations (
    migration_id text primary key,
    applied_at timestamptz not null default now()
);

create table if not exists inventory_sale_attempts (
    platform text not null check (platform in ('iaai', 'copart')),
    lot_key text not null,
    lot_number text not null,
    provider_vehicle_id text,
    provider_lot_id text,
    provider_attempt_id text,
    attempt_key text not null,
    sale_date timestamptz not null,
    status text not null,
    status_id integer,
    bid_usd numeric,
    buy_now_usd numeric,
    final_bid_updated_at timestamptz,
    input_hash text not null,
    source_observed_at timestamptz not null,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    primary key (attempt_key)
);

create unique index if not exists ux_inventory_sale_attempts_provider_id
    on inventory_sale_attempts (platform, provider_attempt_id)
    where provider_attempt_id is not null;
create index if not exists ix_inventory_sale_attempts_lot_date
    on inventory_sale_attempts (lot_key, sale_date desc, attempt_key);
create index if not exists ix_inventory_sale_attempts_status_date
    on inventory_sale_attempts (status, sale_date desc, lot_key);

create table if not exists inventory_sale_attempt_signals_current (
    lot_key text primary key,
    platform text not null check (platform in ('iaai', 'copart')),
    lot_number text not null,
    history_availability text not null,
    attempt_count integer not null,
    not_sold_count integer not null,
    historical_max_bid_usd numeric,
    current_buy_now_usd numeric,
    current_seller_reserve_usd numeric,
    current_ask_usd numeric,
    ask_gap_usd numeric,
    ask_gap_percent numeric,
    buy_now_drop_percent numeric,
    first_attempt_at timestamptz,
    last_attempt_at timestamptz,
    last_not_sold_at timestamptz,
    days_in_cycle integer not null,
    signal_level text not null,
    confidence_percent numeric not null,
    reason_codes jsonb not null default '[]'::jsonb,
    policy_version text not null,
    input_hash text not null,
    source_observed_at timestamptz not null,
    calculated_at timestamptz not null,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

create index if not exists ix_inventory_sale_attempt_signals_rank
    on inventory_sale_attempt_signals_current (signal_level, ask_gap_percent nulls last, not_sold_count desc, lot_key);
create index if not exists ix_inventory_sale_attempt_signals_attempts
    on inventory_sale_attempt_signals_current (not_sold_count desc, lot_key);

insert into schema_migrations (migration_id)
values ('015_sale_attempt_intelligence_v1')
on conflict (migration_id) do nothing;

commit;
