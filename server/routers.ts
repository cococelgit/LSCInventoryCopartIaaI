import { z } from "zod";
import { COOKIE_NAME } from "@shared/const";
import { getSessionCookieOptions } from "./_core/cookies";
import { systemRouter } from "./_core/systemRouter";
import { publicProcedure, router } from "./_core/trpc";
import { getAzure, type AzureVehicle } from "./inventoryApi";
import { internalAuditRouter } from "./routers/internalAudit";

const browseInput = z.object({
  platform: z.enum(["all", "copart", "iaai"]).default("all"),
  query: z.string().trim().max(100).optional(),
  page: z.number().int().min(1).default(1),
  pageSize: z.number().int().min(1).max(100).default(24),
  sort: z.enum(["auction", "bid-low", "bid-high"]).default("auction"),
  yearFrom: z.number().int().min(1900).max(2100).optional(),
  yearTo: z.number().int().min(1900).max(2100).optional(),
  maximumBid: z.number().min(0).max(1_000_000).optional(),
  requireBid: z.boolean().default(false),
  requirePhotos: z.boolean().default(false),
  includeSpecialTitles: z.boolean().default(false),
  makes: z.array(z.string().min(1).max(100)).max(50).default([]),
  models: z.array(z.string().min(1).max(100)).max(50).default([]),
  facilities: z.array(z.string().min(1).max(160)).max(50).default([]),
  states: z.array(z.string().min(1).max(20)).max(50).default([]),
  vehicleTypes: z.array(z.string().min(1).max(100)).max(50).default([]),
  damages: z.array(z.string().min(1).max(100)).max(50).default([]),
  titleTypes: z.array(z.string().min(1).max(100)).max(50).default([]),
  drives: z.array(z.string().min(1).max(100)).max(50).default([]),
  transmissions: z.array(z.string().min(1).max(100)).max(50).default([]),
  fuels: z.array(z.string().min(1).max(100)).max(50).default([]),
  odometerFrom: z.number().min(0).max(2_000_000).optional(),
  odometerTo: z.number().min(0).max(2_000_000).optional(),
  auctionFrom: z.string().regex(/^\d{4}-\d{2}-\d{2}$/).optional(),
  auctionTo: z.string().regex(/^\d{4}-\d{2}-\d{2}$/).optional(),
  estimatedTotalFrom: z.number().min(0).max(1_000_000).optional(),
  estimatedTotalTo: z.number().min(0).max(1_000_000).optional(),
});

type AzureInventoryPage = {
  source: string;
  generatedAt: string;
  page: number;
  pageSize: number;
  total: number;
  totalPages: number;
  vehicles: AzureVehicle[];
};

function browsePath(input: z.infer<typeof browseInput>) {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(input)) {
    if (value === undefined || value === null || value === "") continue;
    if (Array.isArray(value)) value.forEach((entry) => params.append(key, entry));
    else params.set(key, String(value));
  }
  return `/api/v1/inventory/browse?${params.toString()}`;
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
    recent: publicProcedure.input(z.object({ take: z.number().int().min(1).max(1000).default(1000) }).optional()).query(({ input }) => getAzure<{ source: string; generatedAt: string; vehicles: AzureVehicle[] }>(`/api/v1/inventory/recent?take=${input?.take ?? 1000}`)),
    browse: publicProcedure.input(browseInput).query(({ input }) => getAzure<AzureInventoryPage>(browsePath(input))),
    vehicle: publicProcedure.input(z.object({ lot: z.string().min(1).max(32) })).query(({ input }) => getAzure<AzureVehicle>(`/api/v1/inventory/vehicle/${encodeURIComponent(input.lot)}`)),
  }),
  internalAudit: internalAuditRouter,
});

export type AppRouter = typeof appRouter;
