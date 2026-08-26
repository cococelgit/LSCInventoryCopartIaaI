const apiKey = process.env.APIBARA_API_KEY;
if (!apiKey) throw new Error("APIBARA_API_KEY is required");

const response = await fetch("https://apibara.tech/api/v1/vehicle-auction/vehicles/filters", {
  headers: { "X-API-Key": apiKey, Accept: "application/json" },
  signal: AbortSignal.timeout(30_000),
});
if (!response.ok) throw new Error(`filters returned HTTP ${response.status}`);
const payload = await response.json();
const root = payload.data ?? payload;

const allowedGroups = [
  "types",
  "color",
  "fuel_type",
  "transmission",
  "drive_type",
  "running_condition",
  "damage",
  "cylinders",
  "engine_type",
  "has_key",
  "sale_document_filters",
  "seller_type",
  "shipping",
  "location_filters",
];

const sanitize = (value, depth = 0) => {
  if (depth > 4) return "[nested]";
  if (Array.isArray(value)) return value.slice(0, 100).map((entry) => sanitize(entry, depth + 1));
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.entries(value).slice(0, 100).map(([key, entry]) => [key, sanitize(entry, depth + 1)]));
  }
  return value;
};

console.log(JSON.stringify(Object.fromEntries(
  allowedGroups.filter((key) => key in root).map((key) => [key, sanitize(root[key])]),
), null, 2));
