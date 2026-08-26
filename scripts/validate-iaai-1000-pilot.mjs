const token = process.env.INVENTORY_API_TOKEN;
if (!token) throw new Error("INVENTORY_API_TOKEN is not configured");

const baseUrl = "https://ca-lsc-inventory-api-prod.lemoncliff-62ee11e1.eastus2.azurecontainerapps.io";
const getJson = async (path) => {
  const response = await fetch(`${baseUrl}${path}`, { headers: { Authorization: `Bearer ${token}`, Accept: "application/json" }, signal: AbortSignal.timeout(90_000) });
  const text = await response.text();
  if (!response.ok) throw new Error(`${path} returned ${response.status}: ${text.slice(0, 300)}`);
  return JSON.parse(text);
};

const [validation, inventory, discarded] = await Promise.all([
  getJson("/internal/validation"),
  getJson("/api/v1/inventory/recent?take=1000"),
  getJson("/internal/eligibility/discarded?page=1&pageSize=1"),
]);

const vehicles = inventory.vehicles ?? [];
const photoHosts = [...new Set(vehicles.flatMap((vehicle) => vehicle.photos ?? []).map((url) => {
  try { return new URL(url).host; } catch { return null; }
}).filter(Boolean))].sort();
const platforms = Object.fromEntries([...vehicles.reduce((counts, vehicle) => {
  const platform = (vehicle.platform ?? "unknown").toLowerCase();
  counts.set(platform, (counts.get(platform) ?? 0) + 1);
  return counts;
}, new Map()).entries()].sort(([left], [right]) => left.localeCompare(right)));

console.log(JSON.stringify({
  validation: {
    lots: validation.lots,
    versions: validation.versions,
    vinPresent: validation.vinPresent,
    titlePresent: validation.titlePresent,
    damagePresent: validation.damagePresent,
    odometerPresent: validation.odometerPresent,
    auctionDatePresent: validation.auctionDatePresent,
    lotsWithPhotos: validation.lotsWithPhotos,
  },
  publicCut: {
    count: vehicles.length,
    generatedAt: inventory.generatedAt,
    withPhotos: vehicles.filter((vehicle) => (vehicle.photos?.length ?? 0) > 0).length,
    withState: vehicles.filter((vehicle) => vehicle.state).length,
    withTitleType: vehicles.filter((vehicle) => vehicle.titleType).length,
    platforms,
    photoHosts,
  },
  discarded: {
    total: discarded.total,
    ruleSummary: discarded.ruleSummary,
  },
}, null, 2));
