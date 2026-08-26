// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import { createElement } from "react";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";

type LiveAudit = {
  page: number;
  pageSize: number;
  total: number;
  totalPages: number;
  items: Array<{ evaluatedAt: string; evaluation: { decision: string; load_to_system: boolean; lot_number: string | null; auction_source: string | null; vin_masked: string | null; discard_reasons: Array<{ code: string; name: string; explanation: string; source_fields: string[]; observed_values: Record<string, unknown> }>; flags: []; data_quality_notes: string[]; evaluated_fields: string[]; rule_version: string } }>;
  ruleSummary: Array<{ code: string; name: string; count: number }>;
};

let liveAudit: LiveAudit = { page: 1, pageSize: 10, total: 0, totalPages: 0, items: [], ruleSummary: [] };

vi.mock("@/_core/hooks/useAuth", () => ({ useAuth: () => ({ user: { role: "admin", name: "Admin" }, loading: false }) }));
vi.mock("@/components/DashboardLayout", () => ({ default: ({ children }: { children: unknown }) => createElement("div", {}, children as never) }));
vi.mock("@/lib/trpc", () => ({ trpc: { internalAudit: { discarded: { useQuery: () => ({ data: liveAudit, isLoading: false, isError: false }) } } } }));

import InternalDiscards from "./InternalDiscards";

beforeAll(async () => {
  const token = process.env.INVENTORY_API_TOKEN;
  if (!token) throw new Error("INVENTORY_API_TOKEN is required for the live audit test");
  const response = await fetch("https://ca-lsc-inventory-api-prod.lemoncliff-62ee11e1.eastus2.azurecontainerapps.io/internal/eligibility/discarded?page=1&pageSize=10", { headers: { Authorization: `Bearer ${token}`, Accept: "application/json" } });
  if (!response.ok) throw new Error(`Live discard audit returned ${response.status}`);
  liveAudit = await response.json() as LiveAudit;
  if (liveAudit.total === 0 || liveAudit.items.length === 0) throw new Error("Live discard audit is empty");
}, 30_000);

afterEach(() => cleanup());

describe("Internal discard panel with live protected data", () => {
  it("renders the real summary, first row and observed evidence", () => {
    const first = liveAudit.items[0]!;
    const reason = first.evaluation.discard_reasons[0]!;
    render(createElement(InternalDiscards));

    expect(screen.getByText(String(liveAudit.total))).toBeTruthy();
    expect(screen.getByText(`#${first.evaluation.lot_number}`)).toBeTruthy();
    expect(screen.getByText(first.evaluation.vin_masked ?? "N/R")).toBeTruthy();
    expect(screen.getAllByText(reason.name).length).toBeGreaterThan(0);
    expect(document.body.textContent).toContain(first.evaluation.rule_version);

    const evidence = document.querySelector("pre")?.textContent ?? "";
    expect(evidence).toContain(Object.keys(reason.observed_values)[0]);
  });
});
