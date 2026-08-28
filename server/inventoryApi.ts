const INVENTORY_API_BASE_URL = "https://ca-lsc-inventory-api-prod.lemoncliff-62ee11e1.eastus2.azurecontainerapps.io";

export type AzureVehicle = {
  lot: string;
  platform: string;
  observedAt: string;
  title: string | null;
  year: number | null;
  make: string | null;
  model: string | null;
  vehicleType: string | null;
  color: string | null;
  fuelType: string | null;
  transmission: string | null;
  driveType: string | null;
  odometer: number | null;
  damage: string | null;
  auctionAt: string | null;
  lotStatus: string | null;
  currentBidUsd: number | null;
  buyNowUsd: number | null;
  location: string | null;
  state: string | null;
  titleType: string | null;
  titleCode: string | null;
  titleDescriptionEs: string | null;
  titleMappingStatus: string | null;
  titleState: string | null;
  facilityId: string | null;
  vinMasked: string | null;
  sellerName: string | null;
  trim: string | null;
  bodyStyle: string | null;
  engine: string | null;
  cylinders: string | null;
  estimatedRetailValueUsd: number | null;
  repairCostUsd: number | null;
  lotConditionCode: string | null;
  runCondition: string | null;
  hasKeys: boolean | null;
  photos: string[];
};

export async function getAzure<T>(path: string): Promise<T> {
  const token = process.env.INVENTORY_API_TOKEN;
  if (!token) throw new Error("Inventory API token is not configured");
  const response = await fetch(`${INVENTORY_API_BASE_URL}${path}`, {
    headers: { Authorization: `Bearer ${token}`, Accept: "application/json" },
    signal: AbortSignal.timeout(12_000),
  });
  if (!response.ok) throw new Error(`Inventory API returned ${response.status}`);
  return response.json() as Promise<T>;
}
