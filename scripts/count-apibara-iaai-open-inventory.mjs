import { createHash } from "node:crypto";
import { existsSync, readFileSync, writeFileSync } from "node:fs";

const apiKey = process.env.APIBARA_API_KEY;
if (!apiKey) throw new Error("APIBARA_API_KEY is not configured");

const baseUrl = "https://apibara.tech/api/v1/vehicle-auction/vehicles";
const checkpointPath = "/tmp/lsc-iaai-open-count-checkpoint.json";
const resultPath = "/home/ubuntu/lsc-inventory-ui-review/iaai-open-inventory-count.json";
const maxRequests = 12_000;

const checkpoint = existsSync(checkpointPath)
  ? JSON.parse(readFileSync(checkpointPath, "utf8"))
  : { cursor: null, pages: 0, records: 0, hashes: [], facilities: {}, startedAt: new Date().toISOString() };
const hashes = new Set(checkpoint.hashes ?? []);
const facilities = new Map(Object.entries(checkpoint.facilities ?? {}));

const wait = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));
const requestPage = async (cursor) => {
  const url = new URL(baseUrl);
  url.searchParams.set("platform", "iaai");
  url.searchParams.set("lot_sub_status", "Open");
  url.searchParams.set("per_page", "20");
  if (cursor) url.searchParams.set("cursor", cursor);

  let lastError;
  for (let attempt = 1; attempt <= 4; attempt += 1) {
    try {
      const response = await fetch(url, { headers: { "X-API-Key": apiKey, Accept: "application/json" }, signal: AbortSignal.timeout(30_000) });
      if (response.status === 429) throw new Error("Apibara rate limit or quota reached (429)");
      const text = await response.text();
      if (!response.ok) throw new Error(`Apibara returned ${response.status}: ${text.slice(0, 300)}`);
      return JSON.parse(text);
    } catch (error) {
      lastError = error;
      if (attempt < 4) await wait(attempt * 1_000);
    }
  }
  throw lastError;
};

let cursor = checkpoint.cursor ?? null;
let pages = Number(checkpoint.pages ?? 0);
let records = Number(checkpoint.records ?? 0);
let completed = false;

while (pages < maxRequests) {
  const payload = await requestPage(cursor);
  const data = Array.isArray(payload.data) ? payload.data : [];
  for (const vehicle of data) {
    const identity = `${vehicle.platform ?? "iaai"}|${vehicle.lot_number ?? vehicle.slug_vin ?? vehicle.vin ?? JSON.stringify(vehicle)}`;
    hashes.add(createHash("sha256").update(identity).digest("hex"));
    const facility = vehicle.location?.display ?? vehicle.facility?.name ?? "No reportada";
    facilities.set(facility, Number(facilities.get(facility) ?? 0) + 1);
  }
  records += data.length;
  pages += 1;
  cursor = payload.meta?.next_cursor ?? null;

  if (pages % 25 === 0 || !cursor) {
    writeFileSync(checkpointPath, JSON.stringify({ cursor, pages, records, hashes: Array.from(hashes), facilities: Object.fromEntries(facilities), startedAt: checkpoint.startedAt, updatedAt: new Date().toISOString() }));
  }
  if (pages % 100 === 0) console.log(JSON.stringify({ pages, records, uniqueLots: hashes.size, hasNext: Boolean(cursor) }));
  if (!cursor || data.length === 0) {
    completed = true;
    break;
  }
  await wait(150);
}

const result = {
  platform: "iaai",
  lotSubStatus: "Open",
  completed,
  pages,
  requestsUsedForVehiclePages: pages,
  records,
  uniqueLots: hashes.size,
  perPage: 20,
  startedAt: checkpoint.startedAt,
  finishedAt: new Date().toISOString(),
  facilities: Array.from(facilities.entries()).sort((left, right) => Number(right[1]) - Number(left[1])).map(([name, count]) => ({ name, count })),
  nextCursorAvailable: Boolean(cursor),
  safetyLimit: maxRequests,
};
writeFileSync(resultPath, JSON.stringify(result, null, 2));
console.log(JSON.stringify({ ...result, facilities: result.facilities.slice(0, 20) }, null, 2));

if (!completed) process.exitCode = 2;
