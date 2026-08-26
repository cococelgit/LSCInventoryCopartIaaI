const endpoint = "https://ca-lsc-inventory-api-prod.lemoncliff-62ee11e1.eastus2.azurecontainerapps.io/api/v1/usage";
const token = process.env.INVENTORY_API_TOKEN;

if (!token) {
  console.error("INVENTORY_API_TOKEN is not available in this execution environment.");
  process.exit(2);
}

const response = await fetch(endpoint, {
  headers: { Authorization: `Bearer ${token}` },
  signal: AbortSignal.timeout(30_000),
});

if (!response.ok) {
  console.error(`Usage endpoint returned HTTP ${response.status}.`);
  process.exit(1);
}

const payload = await response.json();
console.log(JSON.stringify(payload, null, 2));
