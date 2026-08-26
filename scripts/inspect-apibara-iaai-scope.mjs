const apiKey = process.env.APIBARA_API_KEY;
if (!apiKey) throw new Error("APIBARA_API_KEY is not configured");

const baseUrl = "https://apibara.tech/api/v1/vehicle-auction/";
const getJson = async (path) => {
  const response = await fetch(new URL(path, baseUrl), { headers: { "X-API-Key": apiKey, Accept: "application/json" }, signal: AbortSignal.timeout(30_000) });
  const text = await response.text();
  if (!response.ok) throw new Error(`${path} returned ${response.status}: ${text.slice(0, 300)}`);
  return JSON.parse(text);
};

const usageBefore = await getJson("usage");
const vehicles = await getJson("vehicles?platform=iaai&per_page=20");
const usageAfter = await getJson("usage");

const data = Array.isArray(vehicles.data) ? vehicles.data : [];
const photoUrls = data.flatMap((vehicle) => Array.isArray(vehicle.media?.thumbs) ? vehicle.media.thumbs : []);
const photoHosts = [...new Set(photoUrls.map((url) => {
  try { return new URL(url).host; } catch { return null; }
}).filter(Boolean))].sort();
const states = [...new Set(data.map((vehicle) => vehicle.facility?.state ?? vehicle.location?.state).filter(Boolean))].sort();
const facilities = [...new Map(data.map((vehicle) => {
  const id = vehicle.facility?.id ?? vehicle.location?.facility_id ?? null;
  const name = vehicle.facility?.name ?? vehicle.location?.display ?? null;
  return id || name ? [String(id ?? name), { id, name, state: vehicle.facility?.state ?? vehicle.location?.state ?? null }] : null;
}).filter(Boolean)).values()];

const summarizeUsage = (payload) => {
  const value = payload?.data ?? payload;
  return { plan: value?.plan ?? null, limit: value?.limit ?? value?.quota_limit ?? null, used: value?.used ?? value?.requests_used ?? null, remaining: value?.remaining ?? value?.requests_remaining ?? null };
};

console.log(JSON.stringify({
  pageItems: data.length,
  meta: vehicles.meta ?? null,
  metaKeys: Object.keys(vehicles.meta ?? {}),
  responseKeys: Object.keys(vehicles),
  states,
  facilities,
  photoHosts,
  fieldCoverage: {
    auctionDate: data.filter((vehicle) => vehicle.auction?.auction_at).length,
    photos: data.filter((vehicle) => Array.isArray(vehicle.media?.thumbs) && vehicle.media.thumbs.length > 0).length,
    title: data.filter((vehicle) => vehicle.sale_document?.name).length,
    primaryDamage: data.filter((vehicle) => vehicle.condition?.primary_damage).length,
    seller: data.filter((vehicle) => vehicle.seller?.name).length,
  },
  usageBefore: summarizeUsage(usageBefore),
  usageAfter: summarizeUsage(usageAfter),
}, null, 2));
