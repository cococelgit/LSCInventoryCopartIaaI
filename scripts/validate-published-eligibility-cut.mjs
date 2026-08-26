import { readFileSync } from "node:fs";

const payload = JSON.parse(readFileSync(process.argv[2] ?? "/tmp/lsc-published-eligibility-cut.json", "utf8"));
const result = payload.result?.data?.json ?? payload.result?.data ?? payload;
const vehicles = result.vehicles ?? [];
const normalize = (value) => String(value ?? "").toUpperCase().replace(/[-/_,.]+/g, " ").replace(/\s+/g, " ").trim();
const specialPhrases = ["CERTIFICATE OF DESTRUCTION", "JUNK", "NON REPAIRABLE", "PARTS ONLY"];
const forbiddenDamage = ["UNDERCARRIAGE", "BURN", "FLOOD", "FRAME DAMAGE", "MISSING ALTERED VIN", "REPLACED VIN", "BIOHAZARD CHEMICAL"];
const facilities = [...new Set(vehicles.map((vehicle) => vehicle.facilityId).filter(Boolean))].sort();
const specialTitles = vehicles.filter((vehicle) => specialPhrases.some((phrase) => ` ${normalize(vehicle.titleType)} `.includes(` ${phrase} `)));
const forbiddenVisible = vehicles.filter((vehicle) => forbiddenDamage.some((phrase) => ` ${normalize(vehicle.damage)} `.includes(` ${phrase} `)));
const pendingVisible = vehicles.filter((vehicle) => {
  const title = normalize(vehicle.titleType);
  return title.includes("PENDING TITLE") || title.includes("REPO AFFIDAVIT");
});

console.log(JSON.stringify({
  vehicles: vehicles.length,
  generatedAt: result.generatedAt ?? null,
  facilities: facilities.length,
  facilityIds: facilities,
  states: [...new Set(vehicles.map((vehicle) => vehicle.state).filter(Boolean))].sort(),
  specialTitlesLoaded: specialTitles.length,
  specialTitleExamples: specialTitles.slice(0, 5).map((vehicle) => ({ lot: vehicle.lot, titleType: vehicle.titleType })),
  forbiddenDamageVisible: forbiddenVisible.length,
  pendingTitleVisible: pendingVisible.length,
  fieldCoverage: {
    photos: vehicles.filter((vehicle) => Array.isArray(vehicle.photos) && vehicle.photos.length > 0).length,
    odometer: vehicles.filter((vehicle) => typeof vehicle.odometer === "number").length,
    damage: vehicles.filter((vehicle) => Boolean(vehicle.damage)).length,
    titleType: vehicles.filter((vehicle) => Boolean(vehicle.titleType)).length,
    transmission: vehicles.filter((vehicle) => Boolean(vehicle.transmission)).length,
    driveType: vehicles.filter((vehicle) => Boolean(vehicle.driveType)).length,
    fuelType: vehicles.filter((vehicle) => Boolean(vehicle.fuelType)).length,
    auctionAt: vehicles.filter((vehicle) => Boolean(vehicle.auctionAt)).length,
  },
}, null, 2));
