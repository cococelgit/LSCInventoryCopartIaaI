import { z } from "zod";
import { getAzure } from "../inventoryApi";
import { adminProcedure, router } from "../_core/trpc";

export type DiscardReason = {
  code: string;
  name: string;
  explanation: string;
  source_fields: string[];
  observed_values: Record<string, unknown>;
};

export type DiscardAuditPage = {
  page: number;
  pageSize: number;
  total: number;
  totalPages: number;
  items: Array<{
    evaluatedAt: string;
    evaluation: {
      decision: string;
      load_to_system: boolean;
      lot_number: string | null;
      auction_source: string | null;
      vin_masked: string | null;
      discard_reasons: DiscardReason[];
      flags: DiscardReason[];
      data_quality_notes: string[];
      evaluated_fields: string[];
      rule_version: string;
    };
  }>;
  ruleSummary: Array<{ code: string; name: string; count: number }>;
};

export const internalAuditRouter = router({
  discarded: adminProcedure.input(z.object({
    page: z.number().int().min(1).default(1),
    pageSize: z.number().int().min(10).max(100).default(25),
    ruleCode: z.string().trim().max(8).optional(),
    query: z.string().trim().max(64).optional(),
  })).query(({ input }) => {
    const params = new URLSearchParams({ page: String(input.page), pageSize: String(input.pageSize) });
    if (input.ruleCode) params.set("ruleCode", input.ruleCode);
    if (input.query) params.set("query", input.query);
    return getAzure<DiscardAuditPage>(`/internal/eligibility/discarded?${params.toString()}`);
  }),
});
