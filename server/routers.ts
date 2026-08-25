import { z } from "zod";
import { COOKIE_NAME } from "@shared/const";
import { getSessionCookieOptions } from "./_core/cookies";
import { systemRouter } from "./_core/systemRouter";
import { publicProcedure, router } from "./_core/trpc";

const INVENTORY_API_BASE_URL = "https://ca-lsc-inventory-api-prod.lemoncliff-62ee11e1.eastus2.azurecontainerapps.io";

type AzureVehicle = {
  lot: string;
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
  photos: string[];
};

async function getAzure<T>(path: string): Promise<T> {
  const token = process.env.INVENTORY_API_TOKEN;
  if (!token) throw new Error("Inventory API token is not configured");
  const response = await fetch(`${INVENTORY_API_BASE_URL}${path}`, {
    headers: { Authorization: `Bearer ${token}`, Accept: "application/json" },
    signal: AbortSignal.timeout(12_000),
  });
  if (!response.ok) throw new Error(`Inventory API returned ${response.status}`);
  return response.json() as Promise<T>;
}

export const appRouter = router({
  system: systemRouter,
  auth: router({
    me: publicProcedure.query(opts => opts.ctx.user),
    logout: publicProcedure.mutation(({ ctx }) => {
      const cookieOptions = getSessionCookieOptions(ctx.req);
      ctx.res.clearCookie(COOKIE_NAME, { ...cookieOptions, maxAge: -1 });
      return { success: true } as const;
    }),
  }),
  inventory: router({
    recent: publicProcedure.input(z.object({ take: z.number().int().min(1).max(100).default(100) }).optional()).query(({ input }) => getAzure<{ source: string; generatedAt: string; vehicles: AzureVehicle[] }>(`/api/v1/inventory/recent?take=${input?.take ?? 100}`)),
    vehicle: publicProcedure.input(z.object({ lot: z.string().min(1).max(32) })).query(({ input }) => getAzure<AzureVehicle>(`/api/v1/inventory/vehicle/${encodeURIComponent(input.lot)}`)),
  }),
});

export type AppRouter = typeof appRouter;
