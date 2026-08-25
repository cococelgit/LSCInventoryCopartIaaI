import { z } from "zod";
import { COOKIE_NAME } from "@shared/const";
import { getSessionCookieOptions } from "./_core/cookies";
import { systemRouter } from "./_core/systemRouter";
import { publicProcedure, router } from "./_core/trpc";
import { getAzure, type AzureVehicle } from "./inventoryApi";
import { internalAuditRouter } from "./routers/internalAudit";

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
    vehicle: publicProcedure.input(z.object({ lot: z.string().min(1).max(32) })).query(({ input }) => getAzure<AzureVehicle>(`/api/v1/inventory/vehicle/${encodeURIComponent(input.lot)}`)),
  }),
  internalAudit: internalAuditRouter,
});

export type AppRouter = typeof appRouter;
