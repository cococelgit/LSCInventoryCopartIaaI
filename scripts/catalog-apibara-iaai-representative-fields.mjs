const apiKey = process.env.APIBARA_API_KEY;
if (!apiKey) throw new Error("APIBARA_API_KEY is required");

const base = "https://apibara.tech/api/v1/vehicle-auction";
const headers = { "X-API-Key": apiKey, Accept: "application/json" };
const categories = [
  "AUTOMOBILE",
  "SUV",
  "PICKUP",
  "VAN",
  "RECREATIONAL VEHICLE (RV)",
  "ATV",
  "TRAILERS",
  "BOAT",
  "MOTORCYCLE",
  "HEAVY DUTY TRUCKS",
  "INDUSTRIAL EQUIPMENT",
  "CONSTRUCTION EQUIPMENT",
  "FARM EQUIPMENT",
  "BUS",
  "OTHER",
];

const fetchJson = async (url) => {
  let lastStatus = null;
  for (let attempt = 1; attempt <= 3; attempt += 1) {
    const response = await fetch(url, { headers, signal: AbortSignal.timeout(45_000) });
    lastStatus = response.status;
    if (response.ok) return response.json();
    if (![429, 500, 502, 503, 504].includes(response.status)) break;
    await new Promise((resolve) => setTimeout(resolve, attempt * 1_500));
  }
  throw new Error(`${url.pathname} returned HTTP ${lastStatus}`);
};

const classify = (value) => value === null ? "null" : Array.isArray(value) ? "array" : typeof value;
const collect = (value, path, catalog, sampleKey) => {
  const type = classify(value);
  const entry = catalog.get(path) ?? { path, types: new Set(), presentIn: new Set(), nonNullIn: new Set() };
  entry.types.add(type);
  entry.presentIn.add(sampleKey);
  if (value !== null && value !== undefined) entry.nonNullIn.add(sampleKey);
  catalog.set(path, entry);
  if (type === "object") {
    for (const [key, nested] of Object.entries(value)) collect(nested, path ? `${path}.${key}` : key, catalog, sampleKey);
  } else if (type === "array") {
    for (const nested of value.slice(0, 3)) collect(nested, `${path}[]`, catalog, sampleKey);
  }
};

const catalog = new Map();
const coverage = [];
let requests = 0;
let sampleCount = 0;

for (const category of categories) {
  const listUrl = new URL(`${base}/vehicles`);
  listUrl.searchParams.set("platform", "iaai");
  listUrl.searchParams.set("lot_sub_status", "open");
  listUrl.searchParams.set("type", category);
  listUrl.searchParams.set("per_page", "5");
  let listBody;
  try {
    listBody = await fetchJson(listUrl);
    requests += 1;
  } catch (error) {
    coverage.push({ category, listCount: 0, returnedTypes: [], detailLoaded: false, error: String(error) });
    continue;
  }
  const vehicles = Array.isArray(listBody.data) ? listBody.data : [];
  const returnedTypes = [...new Set(vehicles.map((vehicle) => vehicle.type).filter(Boolean))].sort();
  for (const [index, vehicle] of vehicles.entries()) {
    collect(vehicle, "", catalog, `${category}:list:${index}`);
    sampleCount += 1;
  }

  let detailLoaded = false;
  const identifier = vehicles[0]?.lot_number ?? vehicles[0]?.vin;
  if (identifier) {
    const detailUrl = new URL(`${base}/vehicles/${encodeURIComponent(identifier)}`);
    try {
      const detailBody = await fetchJson(detailUrl);
      requests += 1;
      if (detailBody.data) {
        collect(detailBody.data, "", catalog, `${category}:detail`);
        sampleCount += 1;
        detailLoaded = true;
      }
    } catch {
      detailLoaded = false;
    }
  }

  coverage.push({ category, listCount: vehicles.length, returnedTypes, detailLoaded });
}

const fields = [...catalog.values()]
  .filter((entry) => entry.path)
  .map((entry) => ({
    path: entry.path,
    types: [...entry.types].sort(),
    present: entry.presentIn.size,
    nonNull: entry.nonNullIn.size,
    samples: sampleCount,
  }))
  .sort((a, b) => a.path.localeCompare(b.path));

console.log(JSON.stringify({
  generatedAt: new Date().toISOString(),
  source: "Apibara IAAI representative field-path catalog; identifiers and values intentionally omitted",
  requests,
  sampleCount,
  coverage,
  fields,
}, null, 2));
