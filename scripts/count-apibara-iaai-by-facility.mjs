import { createHash } from "node:crypto";
import { writeFileSync } from "node:fs";

const apiKey = process.env.APIBARA_API_KEY;
if (!apiKey) throw new Error("APIBARA_API_KEY is not configured");

const baseUrl = "https://apibara.tech/api/v1/vehicle-auction/";
const outputPath = "/home/ubuntu/lsc-inventory-ui-review/iaai-open-inventory-count.json";
const headers = { "X-API-Key": apiKey, Accept: "application/json" };
const wait = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));

const getJson = async (url) => {
  let lastError;
  for (let attempt = 1; attempt <= 4; attempt += 1) {
    try {
      const response = await fetch(url, { headers, signal: AbortSignal.timeout(30_000) });
      const text = await response.text();
      if (response.status === 429) throw new Error("Apibara rate limit or quota reached (429)");
      if (!response.ok) throw new Error(`Apibara returned ${response.status}: ${text.slice(0, 300)}`);
      return JSON.parse(text);
    } catch (error) {
      lastError = error;
      if (attempt < 4) await wait(attempt * 1_000);
    }
  }
  throw lastError;
};

const listFacilities = async () => {
  const facilities = [];
  let cursor = null;
  do {
    const url = new URL("locations", baseUrl);
    url.searchParams.set("platform", "iaai");
    url.searchParams.set("per_page", "20");
    if (cursor) url.searchParams.set("cursor", cursor);
    const payload = await getJson(url);
    facilities.push(...(Array.isArray(payload.data) ? payload.data : []));
    cursor = payload.meta?.next_cursor ?? null;
  } while (cursor);
  return facilities;
};

const countFacility = async (facility) => {
  const facilityId = String(facility.facility_id ?? facility.id ?? "").trim();
  if (!facilityId) return { facilityId: null, name: facility.name ?? facility.name_desc ?? "No reportada", state: facility.state_code ?? null, records: 0, pages: 0, uniqueHashes: [] };
  const uniqueHashes = new Set();
  let cursor = null;
  let pages = 0;
  let records = 0;
  do {
    const url = new URL("vehicles", baseUrl);
    url.searchParams.set("platform", "iaai");
    url.searchParams.set("lot_sub_status", "Open");
    url.searchParams.set("facility_id", facilityId);
    url.searchParams.set("per_page", "20");
    if (cursor) url.searchParams.set("cursor", cursor);
    const payload = await getJson(url);
    const data = Array.isArray(payload.data) ? payload.data : [];
    pages += 1;
    records += data.length;
    for (const vehicle of data) {
      const identity = `${vehicle.platform ?? "iaai"}|${vehicle.lot_number ?? vehicle.slug_vin ?? vehicle.vin ?? JSON.stringify(vehicle)}`;
      uniqueHashes.add(createHash("sha256").update(identity).digest("hex"));
    }
    cursor = payload.meta?.next_cursor ?? null;
  } while (cursor);
  return { facilityId, name: facility.name ?? facility.name_desc ?? facility.location_name ?? facilityId, state: facility.state_code ?? facility.state ?? null, records, pages, uniqueHashes: [...uniqueHashes] };
};

const facilities = await listFacilities();
const queue = facilities.slice();
const results = [];
const worker = async () => {
  while (queue.length > 0) {
    const facility = queue.shift();
    const result = await countFacility(facility);
    results.push(result);
    if (results.length % 10 === 0) console.log(JSON.stringify({ facilitiesCompleted: results.length, facilitiesTotal: facilities.length, records: results.reduce((sum, item) => sum + item.records, 0), pages: results.reduce((sum, item) => sum + item.pages, 0) }));
  }
};

await Promise.all(Array.from({ length: Math.min(8, Math.max(1, facilities.length)) }, () => worker()));

const allHashes = new Set(results.flatMap((result) => result.uniqueHashes));
const rawRecords = results.reduce((sum, item) => sum + item.records, 0);
const vehiclePageRequests = results.reduce((sum, item) => sum + item.pages, 0);
const locationPageRequests = Math.ceil(facilities.length / 20);
const report = {
  platform: "iaai",
  lotSubStatus: "Open",
  scope: "all facilities returned by Apibara",
  completed: true,
  facilities: facilities.length,
  facilitiesWithOpenInventory: results.filter((item) => item.records > 0).length,
  rawRecords,
  uniqueLots: allHashes.size,
  duplicateOccurrences: rawRecords - allHashes.size,
  requests: { locationPages: locationPageRequests, vehiclePages: vehiclePageRequests, total: locationPageRequests + vehiclePageRequests },
  perPage: 20,
  finishedAt: new Date().toISOString(),
  byFacility: results.sort((left, right) => right.records - left.records).map(({ uniqueHashes, ...result }) => result),
};
writeFileSync(outputPath, JSON.stringify(report, null, 2));
console.log(JSON.stringify({ ...report, byFacility: report.byFacility.slice(0, 25) }, null, 2));
