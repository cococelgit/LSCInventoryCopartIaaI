const apiKey = process.env.APIBARA_API_KEY;
if (!apiKey) throw new Error("APIBARA_API_KEY is required");

const base = "https://apibara.tech/api/v1/vehicle-auction";
const headers = { "X-API-Key": apiKey, Accept: "application/json" };

const fetchJson = async (url) => {
  const response = await fetch(url, { headers, signal: AbortSignal.timeout(45_000) });
  if (!response.ok) throw new Error(`${url.pathname} returned HTTP ${response.status}`);
  return response.json();
};

const classify = (value) => {
  if (value === null) return "null";
  if (Array.isArray(value)) return "array";
  return typeof value;
};

const collect = (value, path, catalog, sampleIndex) => {
  const type = classify(value);
  const entry = catalog.get(path) ?? { path, types: new Set(), presentIn: new Set(), nonNullIn: new Set() };
  entry.types.add(type);
  entry.presentIn.add(sampleIndex);
  if (value !== null && value !== undefined) entry.nonNullIn.add(sampleIndex);
  catalog.set(path, entry);

  if (type === "object") {
    for (const [key, nested] of Object.entries(value)) {
      collect(nested, path ? `${path}.${key}` : key, catalog, sampleIndex);
    }
  } else if (type === "array") {
    for (const nested of value.slice(0, 3)) {
      collect(nested, `${path}[]`, catalog, sampleIndex);
    }
  }
};

const listUrl = new URL(`${base}/vehicles`);
listUrl.searchParams.set("platform", "iaai");
listUrl.searchParams.set("lot_sub_status", "open");
listUrl.searchParams.set("per_page", "20");

const listBody = await fetchJson(listUrl);
const listVehicles = Array.isArray(listBody.data) ? listBody.data : [];
if (listVehicles.length === 0) throw new Error("IAAI list sample is empty");

const detailVehicles = [];
for (const vehicle of listVehicles.slice(0, 5)) {
  const identifier = vehicle.lot_number ?? vehicle.vin;
  if (!identifier) continue;
  const detailUrl = new URL(`${base}/vehicles/${encodeURIComponent(identifier)}`);
  const detailBody = await fetchJson(detailUrl);
  if (detailBody.data) detailVehicles.push(detailBody.data);
}

const serializeCatalog = (vehicles) => {
  const catalog = new Map();
  vehicles.forEach((vehicle, index) => collect(vehicle, "", catalog, index));
  return [...catalog.values()]
    .filter((entry) => entry.path)
    .map((entry) => ({
      path: entry.path,
      types: [...entry.types].sort(),
      present: entry.presentIn.size,
      nonNull: entry.nonNullIn.size,
      samples: vehicles.length,
    }))
    .sort((a, b) => a.path.localeCompare(b.path));
};

console.log(JSON.stringify({
  generatedAt: new Date().toISOString(),
  source: "Apibara IAAI field-path catalog; identifiers and values intentionally omitted",
  requests: 1 + detailVehicles.length,
  listSampleCount: listVehicles.length,
  detailSampleCount: detailVehicles.length,
  listFields: serializeCatalog(listVehicles),
  detailFields: serializeCatalog(detailVehicles),
}, null, 2));
