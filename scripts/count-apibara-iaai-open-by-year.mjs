import { createHash } from "node:crypto";
import { writeFileSync } from "node:fs";

const apiKey = process.env.APIBARA_API_KEY;
if (!apiKey) throw new Error("APIBARA_API_KEY is not configured");

const years = Array.from({ length: 128 }, (_, index) => 1900 + index);
const baseUrl = "https://apibara.tech/api/v1/vehicle-auction/vehicles";
const outputPath = "/home/ubuntu/lsc-inventory-ui-review/iaai-open-inventory-count.json";
const headers = { "X-API-Key": apiKey, Accept: "application/json" };
const maxVehicleRequests = 20_000;
let vehicleRequests = 0;
const wait = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));

const getJson = async (url) => {
  let lastError;
  for (let attempt = 1; attempt <= 5; attempt += 1) {
    if (vehicleRequests >= maxVehicleRequests) throw new Error(`Safety limit of ${maxVehicleRequests} vehicle requests reached`);
    vehicleRequests += 1;
    try {
      const response = await fetch(url, { headers, signal: AbortSignal.timeout(45_000) });
      const text = await response.text();
      if (response.status === 429) throw new Error("Apibara rate limit or quota reached (429)");
      if (!response.ok) throw new Error(`Apibara returned ${response.status}: ${text.slice(0, 300)}`);
      return JSON.parse(text);
    } catch (error) {
      lastError = error;
      if (attempt < 5) await wait(attempt * 1_500);
    }
  }
  throw lastError;
};

const countYear = async (year) => {
  const uniqueHashes = new Set();
  const facilities = new Map();
  let cursor = null;
  let pages = 0;
  let records = 0;
  do {
    const url = new URL(baseUrl);
    url.searchParams.set("platform", "iaai");
    url.searchParams.set("lot_sub_status", "Open");
    url.searchParams.set("year_from", String(year));
    url.searchParams.set("year_to", String(year));
    url.searchParams.set("per_page", "20");
    if (cursor) url.searchParams.set("cursor", cursor);
    const payload = await getJson(url);
    const data = Array.isArray(payload.data) ? payload.data : [];
    pages += 1;
    records += data.length;
    for (const vehicle of data) {
      const identity = `${vehicle.platform ?? "iaai"}|${vehicle.lot_number ?? vehicle.slug_vin ?? vehicle.vin ?? JSON.stringify(vehicle)}`;
      uniqueHashes.add(createHash("sha256").update(identity).digest("hex"));
      const facility = vehicle.location?.display ?? vehicle.facility?.name ?? "No reportada";
      facilities.set(facility, Number(facilities.get(facility) ?? 0) + 1);
    }
    cursor = payload.meta?.next_cursor ?? null;
  } while (cursor);
  return { year, records, uniqueLots: uniqueHashes.size, pages, hashes: [...uniqueHashes], facilities: Object.fromEntries(facilities) };
};

const queue = years.slice().reverse();
const results = [];
const worker = async () => {
  while (queue.length > 0) {
    const year = queue.shift();
    const result = await countYear(year);
    results.push(result);
    console.log(JSON.stringify({ yearsCompleted: results.length, yearsTotal: years.length, year, yearRecords: result.records, cumulativeRecords: results.reduce((sum, item) => sum + item.records, 0), requests: vehicleRequests }));
  }
};

await Promise.all(Array.from({ length: 16 }, () => worker()));
const allHashes = new Set(results.flatMap((result) => result.hashes));
const rawRecords = results.reduce((sum, result) => sum + result.records, 0);
const report = {
  platform: "iaai",
  lotSubStatus: "Open",
  scope: "all records with model year 1900–2027",
  completed: true,
  rawRecords,
  uniqueLots: allHashes.size,
  duplicateOccurrences: rawRecords - allHashes.size,
  requestsUsedForVehiclePages: vehicleRequests,
  perPage: 20,
  finishedAt: new Date().toISOString(),
  byYear: results.sort((left, right) => right.year - left.year).map(({ hashes, ...result }) => result),
};
writeFileSync(outputPath, JSON.stringify(report, null, 2));
console.log(JSON.stringify({ ...report, byYear: report.byYear.map(({ facilities, ...result }) => result) }, null, 2));
