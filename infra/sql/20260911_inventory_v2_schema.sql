select pg_advisory_xact_lock(hashtext('lsc:inventory-v2-schema:v1'));

create table if not exists inventory_current_v2 (
    platform text not null,
    lot_number text not null,
    lot_key text not null,
    source_provider text not null default 'auctionsapi',
    source_contract_version text not null default 'auctionsapi-v2',
    domain_id text,
    provider_vehicle_id text,
    provider_lot_id text,
    vin text,
    year integer,
    manufacturer_id text,
    make text,
    model_id text,
    model text,
    generation_id text,
    generation text,
    vehicle_type_id text,
    vehicle_type text,
    body_type_id text,
    body_style text,
    fuel_id text,
    fuel_type text,
    engine_id text,
    engine text,
    engine_size_liters numeric(6,2),
    horsepower numeric(8,2),
    cylinders integer,
    transmission_id text,
    transmission text,
    drive_id text,
    drive_type text,
    exterior_color text,
    manufactured_in text,
    vehicle_class text,
    series text,
    condition_id text,
    condition_name text,
    primary_damage_id text,
    primary_damage text,
    secondary_damage_id text,
    secondary_damage text,
    loss_type text,
    run_condition_value text,
    run_condition_label text,
    run_condition_class_hint text,
    has_key boolean,
    airbags text,
    restraint_system text,
    odometer_miles numeric(14,1),
    odometer_km numeric(14,1),
    odometer_status text,
    title_id text,
    title_type text,
    detailed_title_id text,
    detailed_title text,
    title_group text,
    title_pending boolean,
    title_export boolean,
    title_registration boolean,
    title_page_id text,
    title_brand text,
    title_notes text,
    special_note text,
    announcements text,
    seller_name text,
    seller_type_id text,
    seller_type text,
    seller_class text,
    seller_text_class text,
    seller_is_insurance boolean,
    seller_is_rental boolean,
    seller_is_credit_company boolean,
    seller_classification_confidence numeric(6,5),
    seller_needs_review boolean,
    seller_classification_evidence text,
    seller_taxonomy_version text,
    auction_state text,
    lot_status_id text,
    lot_status text,
    lot_sub_status text,
    auction_at timestamptz,
    auction_at_updated_at timestamptz,
    archived_at timestamptz,
    is_buy_now boolean,
    is_timed boolean,
    current_bid_usd numeric(14,2),
    current_bid_updated_at timestamptz,
    pre_bid_usd numeric(14,2),
    buy_now_usd numeric(14,2),
    buy_now_updated_at timestamptz,
    final_bid_usd numeric(14,2),
    final_bid_updated_at timestamptz,
    sale_price_usd numeric(14,2),
    sale_price_updated_at timestamptz,
    provider_estimate_from_usd numeric(14,2),
    provider_estimate_to_usd numeric(14,2),
    provider_estimate_text text,
    actual_cash_value_usd numeric(14,2),
    estimated_repair_cost_usd numeric(14,2),
    location_display text,
    location_state text,
    facility_id text,
    facility_office_name text,
    facility_zip text,
    send_from text,
    lane text,
    aisle text,
    media_photos_count integer not null default 0,
    media_has_photos boolean not null default false,
    media_has_360 boolean,
    media_has_video boolean,
    is_active boolean not null default true,
    consecutive_misses integer not null default 0,
    first_seen_at timestamptz not null,
    last_seen_at timestamptz not null,
    deactivated_at timestamptz,
    source_created_at timestamptz,
    source_updated_at timestamptz,
    identity_hash text not null,
    spec_hash text not null,
    condition_hash text not null,
    auction_hash text not null,
    seller_location_hash text not null,
    media_hash text not null,
    score_input_hash text not null,
    search_hash text not null,
    record_version bigint not null default 1,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    primary key (platform, lot_number),
    constraint ck_inventory_current_v2_platform check (length(btrim(platform)) > 0),
    constraint ck_inventory_current_v2_lot_number check (length(btrim(lot_number)) > 0),
    constraint ck_inventory_current_v2_year check (year is null or year between 1886 and 2200),
    constraint ck_inventory_current_v2_odometer_miles check (odometer_miles is null or odometer_miles >= 0),
    constraint ck_inventory_current_v2_odometer_km check (odometer_km is null or odometer_km >= 0),
    constraint ck_inventory_current_v2_media_count check (media_photos_count >= 0),
    constraint ck_inventory_current_v2_misses check (consecutive_misses >= 0)
);

create unique index if not exists ux_inventory_current_v2_lot_key
    on inventory_current_v2 (lot_key);
create index if not exists ix_inventory_current_v2_vin
    on inventory_current_v2 (vin) where vin is not null;
create index if not exists ix_inventory_current_v2_active_auction
    on inventory_current_v2 (auction_at, platform, lot_number) where is_active;
create index if not exists ix_inventory_current_v2_active_make_model_year
    on inventory_current_v2 (make, model, year, lot_number) where is_active;
create index if not exists ix_inventory_current_v2_active_location
    on inventory_current_v2 (location_state, facility_id, auction_at, lot_number) where is_active;
create index if not exists ix_inventory_current_v2_active_buy_now
    on inventory_current_v2 (buy_now_usd, lot_number)
    where is_active and buy_now_usd is not null;
create index if not exists ix_inventory_current_v2_inactive_retention
    on inventory_current_v2 (deactivated_at, platform, lot_number)
    where not is_active and deactivated_at is not null;

create table if not exists inventory_media_current_v2 (
    platform text not null,
    lot_number text not null,
    media_type text not null,
    position integer not null,
    source_url text not null,
    thumbnail_url text,
    large_url text,
    observed_at timestamptz not null,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    primary key (platform, lot_number, media_type, position),
    constraint ck_inventory_media_current_v2_position check (position >= 0),
    constraint ck_inventory_media_current_v2_url check (length(btrim(source_url)) > 0)
);

create index if not exists ix_inventory_media_current_v2_lot
    on inventory_media_current_v2 (platform, lot_number, position);

create table if not exists inventory_sale_attempts_v2 (
    attempt_key text primary key,
    platform text not null,
    lot_number text not null,
    vin text,
    sale_date timestamptz,
    sale_date_updated_at timestamptz,
    status_id text,
    status text,
    current_bid_usd numeric(14,2),
    buy_now_usd numeric(14,2),
    final_bid_usd numeric(14,2),
    sale_price_usd numeric(14,2),
    outcome text,
    archived_at timestamptz,
    source_updated_at timestamptz,
    first_observed_at timestamptz not null,
    last_observed_at timestamptz not null,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    constraint ck_inventory_sale_attempts_v2_platform check (length(btrim(platform)) > 0),
    constraint ck_inventory_sale_attempts_v2_lot_number check (length(btrim(lot_number)) > 0)
);

create index if not exists ix_inventory_sale_attempts_v2_lot_date
    on inventory_sale_attempts_v2 (platform, lot_number, sale_date desc, last_observed_at desc);
create index if not exists ix_inventory_sale_attempts_v2_vin_date
    on inventory_sale_attempts_v2 (vin, sale_date desc) where vin is not null;

create table if not exists inventory_tombstones_v2 (
    platform text not null,
    lot_number text not null,
    vin_hash text,
    final_status text,
    final_sale_date timestamptz,
    archived_at timestamptz,
    deleted_at timestamptz not null,
    source_updated_at timestamptz,
    last_auction_hash text,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    primary key (platform, lot_number)
);

create index if not exists ix_inventory_tombstones_v2_deleted
    on inventory_tombstones_v2 (deleted_at desc);
create index if not exists ix_inventory_tombstones_v2_vin_hash
    on inventory_tombstones_v2 (vin_hash) where vin_hash is not null;

create table if not exists inventory_sync_checkpoints_v2 (
    platform text not null,
    stream text not null,
    cursor text,
    high_watermark timestamptz,
    overlap_from timestamptz,
    cycle_id uuid,
    page_number integer not null default 0,
    pages_completed bigint not null default 0,
    lots_observed bigint not null default 0,
    requests_made bigint not null default 0,
    lease_owner uuid,
    lease_expires_at timestamptz,
    heartbeat_at timestamptz,
    status text not null default 'idle',
    last_error text,
    last_started_at timestamptz,
    last_completed_at timestamptz,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    primary key (platform, stream),
    constraint ck_inventory_sync_checkpoints_v2_stream check (stream in ('changed', 'archived')),
    constraint ck_inventory_sync_checkpoints_v2_page check (page_number >= 0),
    constraint ck_inventory_sync_checkpoints_v2_counts check (pages_completed >= 0 and lots_observed >= 0 and requests_made >= 0)
);

create index if not exists ix_inventory_sync_checkpoints_v2_lease
    on inventory_sync_checkpoints_v2 (lease_expires_at)
    where lease_owner is not null;

create table if not exists inventory_v2_schema_state (
    schema_name text primary key,
    schema_version integer not null,
    writer_enabled boolean not null default false,
    reader_enabled boolean not null default false,
    prepared_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    constraint ck_inventory_v2_schema_state_flags check (not reader_enabled or writer_enabled)
);

insert into inventory_v2_schema_state (schema_name, schema_version, writer_enabled, reader_enabled)
values ('inventory-current-v2', 1, false, false)
on conflict (schema_name) do update set
    schema_version = greatest(inventory_v2_schema_state.schema_version, excluded.schema_version),
    updated_at = now();
