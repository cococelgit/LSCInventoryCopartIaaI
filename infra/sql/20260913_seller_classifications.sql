-- Seller classification cache and audit trail.
-- PostgreSQL Flexible Server only. Apply through the Production migration path.

create table if not exists public.seller_classifications (
    id bigserial primary key,
    platform text not null default 'unknown',
    seller_name_normalized text not null,
    seller_name_raw_last_seen text,
    category text not null default 'unknown',
    confidence numeric(5,4) not null default 0,
    needs_review boolean not null default true,
    reason text,
    evidence_type text not null default 'insufficient_evidence',
    model text,
    prompt_version text not null default 'seller_classifier_v1',
    first_seen_at timestamptz not null default now(),
    last_seen_at timestamptz not null default now(),
    classified_at timestamptz,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    constraint seller_classifications_category_ck check (category in ('insurance','dealer','finance','rental_fleet','government','repossession_bank','other','unknown','unclassified')),
    constraint seller_classifications_confidence_ck check (confidence >= 0 and confidence <= 1),
    constraint seller_classifications_evidence_ck check (evidence_type in ('deterministic_rule','provider_field','name_only','insufficient_evidence')),
    constraint seller_classifications_platform_name_uq unique (platform, seller_name_normalized)
);

create index if not exists seller_classifications_category_idx
    on public.seller_classifications (category);

create index if not exists seller_classifications_review_idx
    on public.seller_classifications (needs_review, updated_at);

create index if not exists seller_classifications_name_idx
    on public.seller_classifications (seller_name_normalized);

comment on table public.seller_classifications is 'Auditable seller-name classification cache used by Inventory V2; not vehicle inventory data.';
comment on column public.seller_classifications.seller_name_normalized is 'Stable normalized cache key; raw provider text is retained separately.';
comment on column public.seller_classifications.evidence_type is 'deterministic_rule, provider_field, name_only, or insufficient_evidence.';
comment on column public.seller_classifications.prompt_version is 'Prompt/taxonomy version used for model classifications.';
