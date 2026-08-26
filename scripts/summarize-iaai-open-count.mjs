import { readFileSync, writeFileSync } from "node:fs";

const sourcePath = "/home/ubuntu/lsc-inventory-ui-review/iaai-open-inventory-count.json";
const outputPath = "/home/ubuntu/lsc-inventory-ui-review/iaai-open-inventory-summary.json";
const report = JSON.parse(readFileSync(sourcePath, "utf8"));
const facilities = new Map();
const stateCodes = new Set();

for (const year of report.byYear ?? []) {
  for (const [name, count] of Object.entries(year.facilities ?? {})) {
    facilities.set(name, Number(facilities.get(name) ?? 0) + Number(count));
    const state = name.match(/\(([A-Z]{2})\)$/)?.[1];
    if (state) stateCodes.add(state);
  }
}

const summary = {
  uniqueOpenLots: report.uniqueLots,
  rawRecords: report.rawRecords,
  duplicateOccurrences: report.duplicateOccurrences,
  vehiclePageRequests: report.requestsUsedForVehiclePages,
  distinctFacilitiesObserved: facilities.size,
  distinctStateCodesObserved: stateCodes.size,
  stateCodes: [...stateCodes].sort(),
  topFacilities: [...facilities.entries()].sort((left, right) => right[1] - left[1]).slice(0, 25).map(([name, count]) => ({ name, count })),
  yearsWithInventory: (report.byYear ?? []).filter((year) => year.records > 0).length,
  finishedAt: report.finishedAt,
};
writeFileSync(outputPath, JSON.stringify(summary, null, 2));
console.log(JSON.stringify(summary, null, 2));
