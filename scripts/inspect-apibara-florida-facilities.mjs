const apiKey = process.env.APIBARA_API_KEY;
if (!apiKey) {
  console.error("APIBARA_API_KEY is not available in this execution environment.");
  process.exit(2);
}

const url = new URL("https://apibara.tech/api/v1/vehicle-auction/locations");
url.searchParams.set("platform", "copart");
url.searchParams.set("state", "FL");
url.searchParams.set("per_page", "20");

const response = await fetch(url, {
  headers: { "X-API-Key": apiKey, Accept: "application/json" },
  signal: AbortSignal.timeout(30_000),
});

if (!response.ok) {
  console.error(`Locations endpoint returned HTTP ${response.status}.`);
  process.exit(1);
}

const payload = await response.json();
console.log(JSON.stringify({
  data: (payload.data ?? []).map((location) => ({
    facilityId: location.facility_id ?? location.facilityId,
    display: location.display,
    state: location.state,
    city: location.city,
  })),
  meta: payload.meta ?? null,
}, null, 2));
