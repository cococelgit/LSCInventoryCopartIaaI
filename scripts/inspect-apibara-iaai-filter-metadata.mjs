const apiKey = process.env.APIBARA_API_KEY;
if (!apiKey) throw new Error("APIBARA_API_KEY is not configured");

const response = await fetch("https://apibara.tech/api/v1/vehicle-auction/vehicles/filters", {
  headers: { "X-API-Key": apiKey, Accept: "application/json" },
  signal: AbortSignal.timeout(30_000),
});
if (!response.ok) throw new Error(`filters returned ${response.status}`);
const payload = await response.json();
const root = payload.data ?? payload;

const candidates = Object.entries(root).filter(([key]) => /platform|auction|status|state|facility|count|total|ranges/i.test(key));
const summarize = (value, depth = 0) => {
  if (depth > 3) return "[nested]";
  if (Array.isArray(value)) return value.slice(0, 30).map((item) => summarize(item, depth + 1));
  if (value && typeof value === "object") return Object.fromEntries(Object.entries(value).slice(0, 40).map(([key, item]) => [key, summarize(item, depth + 1)]));
  return value;
};

console.log(JSON.stringify({ rootKeys: Object.keys(root), candidates: Object.fromEntries(candidates.map(([key, value]) => [key, summarize(value)])) }, null, 2));
