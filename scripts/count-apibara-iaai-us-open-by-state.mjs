import { createHash } from "node:crypto";
import { writeFileSync } from "node:fs";

const apiKey = process.env.APIBARA_API_KEY;
if (!apiKey) throw new Error("APIBARA_API_KEY is not configured");

const states = ["AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA","HI","ID","IL","IN","IA","KS","KY","LA","ME","MD","MA","MI","MN","MS","MO","MT","NE","NV","NH","NJ","NM","NY","NC","ND","OH","OK","OR","PA","RI","SC","SD","TN","TX","UT","VT","VA","WA","WV","WI","WY","DC","PR","GU","VI"];
const baseUrl = "https://apibara.tech/api/v1/vehicle-auction/vehicles";
const outputPath = "/home/ubuntu/lsc-inventory-ui-review/iaai-us-open-inventory-count.json";
const headers = { "X-API-Key": apiKey, Accept: "application/json" };
const wait = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));

const getJson = async (url) => {
  let lastError;
  for (let attempt = 1; attempt <= 5; attempt += 1) {
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

const countState = async (state) => {
  const uniqueHashes = new Set();
  const facilities = new Map();
  let cursor = null;
  let pages = 0;
  let records = 0;
  do {
    const url = new URL(baseUrl);
    url.searchParams.set("platform", "iaai");
    url.searchParams.set("lot_sub_status", "Open");
    url.searchParams.set("loc_state", state);
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
  return { state, records, uniqueLots: uniqueHashes.size, pages, hashes: [...uniqueHashes], facilities: Object.fromEntries(facilities) };
};

const queue = states.slice();
const results = [];
const worker = async () => {
  while (queue.length > 0) {
    const state = queue.shift();
    const result = await countState(state);
    results.push(result);
    console.log(JSON.stringify({ statesCompleted: results.length, statesTotal: states.length, state, stateRecords: result.records, cumulativeRecords: results.reduce((sum, item) => sum + item.records, 0), cumulativePages: results.reduce((sum, item) => sum + item.pages, 0) }));
  }
};

await Promise.all(Array.from({ length: 12 }, () => worker()));
const allHashes = new Set(results.flatMap((result) => result.hashes));
const rawRecords = results.reduce((sum, result) => sum + result.records, 0);
const pages = results.reduce((sum, result) => sum + result.pages, 0);
const report = {
  platform: "iaai",
  lotSubStatus: "Open",
  scope: "United States: 50 states, DC, PR, GU and VI",
  completed: true,
  rawRecords,
  uniqueLots: allHashes.size,
  duplicateOccurrences: rawRecords - allHashes.size,
  requestsUsedForVehiclePages: pages,
  perPage: 20,
  finishedAt: new Date().toISOString(),
  byState: results.sort((left, right) => right.records - left.records).map(({ hashes, ...result }) => result),
};
writeFileSync(outputPath, JSON.stringify(report, null, 2));
console.log(JSON.stringify({ ...report, byState: report.byState.map(({ facilities, ...result }) => result) }, null, 2));
