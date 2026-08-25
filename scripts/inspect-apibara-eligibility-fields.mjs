const apiKey = process.env.APIBARA_API_KEY;
const lot = process.argv[2] ?? "75803205";

if (!apiKey) {
  console.error("APIBARA_API_KEY is not available in this execution environment.");
  process.exit(2);
}

const url = new URL(`https://apibara.tech/api/v1/vehicle-auction/vehicles/${encodeURIComponent(lot)}`);
const response = await fetch(url, {
  headers: { "X-API-Key": apiKey, Accept: "application/json" },
  signal: AbortSignal.timeout(30_000),
});

if (!response.ok) {
  console.error(`Vehicle detail endpoint returned HTTP ${response.status}.`);
  process.exit(1);
}

const body = await response.json();
const vehicle = body.data ?? {};
const maskVin = (vin) => typeof vin === "string" && vin.trim()
  ? `***${vin.trim().slice(-4)}`
  : null;
const pick = (value) => value === undefined ? null : value;

console.log(JSON.stringify({
  topLevelKeys: Object.keys(vehicle).sort(),
  nestedKeys: {
    auction: Object.keys(vehicle.auction ?? {}).sort(),
    condition: Object.keys(vehicle.condition ?? {}).sort(),
    details: Object.keys(vehicle.details ?? {}).sort(),
    facility: Object.keys(vehicle.facility ?? {}).sort(),
    location: Object.keys(vehicle.location ?? {}).sort(),
    sale_document: Object.keys(vehicle.sale_document ?? {}).sort(),
    seller: Object.keys(vehicle.seller ?? {}).sort(),
  },
  normalizedCandidate: {
    lot_number: pick(vehicle.lot_number),
    auction_source: pick(vehicle.platform),
    vin_masked: maskVin(vehicle.vin),
    sale_date_candidates: {
      sale_date: pick(vehicle.sale_date),
      auction_at: pick(vehicle.auction?.auction_at),
    },
    location_state_candidates: {
      top_level_state: pick(vehicle.state),
      auction_state: pick(vehicle.auction?.state),
      location_state: pick(vehicle.location?.state),
    },
    seller_name_candidates: {
      seller_name: pick(vehicle.seller_name),
      seller: pick(vehicle.seller),
      details_seller: pick(vehicle.details?.seller_name ?? vehicle.details?.seller),
    },
    damage_candidates: {
      damage_description: pick(vehicle.damage_description),
      primary_damage: pick(vehicle.condition?.primary_damage),
      secondary_damage: pick(vehicle.secondary_damage ?? vehicle.condition?.secondary_damage),
    },
    title_candidates: {
      sale_title_type_code: pick(vehicle.sale_title_type_code),
      sale_title_type_label: pick(vehicle.sale_title_type_label),
      sale_title_type_description: pick(vehicle.sale_title_type_description),
      sale_document: pick(vehicle.sale_document),
      title_notes: pick(vehicle.title_notes),
      special_note: pick(vehicle.special_note),
      announcements: pick(vehicle.announcements),
      details: pick(vehicle.details),
    },
  },
}, null, 2));
