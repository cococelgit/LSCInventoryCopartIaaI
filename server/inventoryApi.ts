const INVENTORY_API_BASE_URL = "https://ca-lsc-inventory-api-prod.lemoncliff-62ee11e1.eastus2.azurecontainerapps.io";

export type AzureVehicle = {
  lot: string;
  platform: string;
  observedAt: string;
  title: string | null;
  year: number | null;
  make: string | null;
  model: string | null;
  series: string | null;
  vehicleType: string | null;
  bodyStyle: string | null;
  color: string | null;
  fuelType: string | null;
  transmission: string | null;
  driveType: string | null;
  odometer: number | null;
  odometerKm: number | null;
  odometerStatus: string | null;
  damage: string | null;
  secondaryDamage: string | null;
  lossType: string | null;
  startCode: string | null;
  hasKey: boolean | null;
  auctionAt: string | null;
  lotStatus: string | null;
  lotSubStatus: string | null;
  isBuyNow: boolean | null;
  isTimed: boolean | null;
  currentBidUsd: number | null;
  preBidUsd: number | null;
  buyNowUsd: number | null;
  estimatedPriceFromUsd: number | null;
  estimatedPriceToUsd: number | null;
  estimatedPriceText: string | null;
  actualCashValueUsd: number | null;
  estimatedRepairCostUsd: number | null;
  location: string | null;
  sendFrom: string | null;
  state: string | null;
  facilityId: string | null;
  sellingBranch: string | null;
  lane: string | null;
  aisle: string | null;
  sellerName: string | null;
  sellerType: string | null;
  titleType: string | null;
  saleDocumentType: string | null;
  saleDocumentGroup: string | null;
  saleDocumentPending: boolean | null;
  saleDocumentExport: boolean | null;
  saleDocumentRegistration: boolean | null;
  titleBrand: string | null;
  titleNotes: string | null;
  engineSizeLiters: string | null;
  engineHorsepower: number | null;
  engineLayout: string | null;
  engineDescription: string | null;
  cylinders: string | null;
  airbags: string | null;
  restraintSystem: string | null;
  vinStatus: string | null;
  vehicleClass: string | null;
  vehicleScore: string | null;
  manufacturedIn: string | null;
  options: string | null;
  has360: boolean | null;
  hasVideo: boolean | null;
  photos: string[];
  media: Array<{ url: string; type: string | null }>;
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
