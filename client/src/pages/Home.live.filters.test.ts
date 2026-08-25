// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { createElement } from "react";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";

type LiveVehicle = {
  auctionAt: string | null;
  currentBidUsd: number | null;
};

let liveInventory: { source: string; generatedAt: string; vehicles: LiveVehicle[] } = {
  source: "pending-live-cut",
  generatedAt: "",
  vehicles: [],
};

vi.mock("../lib/trpc", () => ({
  trpc: {
    inventory: {
      recent: {
        useQuery: () => ({ data: liveInventory, isLoading: false }),
      },
    },
  },
}));

import Home, { auctionDateInEastern } from "./Home";

const addOneDay = (dateKey: string) => {
  const date = new Date(`${dateKey}T12:00:00Z`);
  date.setUTCDate(date.getUTCDate() + 1);
  return date.toISOString().slice(0, 10);
};

beforeAll(async () => {
  const response = await fetch("https://lsc-inv-revi-zyn4tlbw.manus.space/api/trpc/inventory.recent?input=%7B%22json%22%3A%7B%22take%22%3A100%7D%7D");
  if (!response.ok) throw new Error(`Live inventory bridge returned ${response.status}`);
  const payload = await response.json() as { result?: { data?: { json?: typeof liveInventory } } };
  const parsed = payload.result?.data?.json;
  if (!parsed || parsed.vehicles.length === 0) throw new Error("Live inventory cut is empty");
  liveInventory = parsed;
});

afterEach(() => cleanup());

describe("Home filters with the live published inventory cut", () => {
  it("applies a real auction-date range and reduces live results", () => {
    const latestDate = liveInventory.vehicles
      .map((vehicle) => auctionDateInEastern(vehicle.auctionAt))
      .filter((date): date is string => date !== null)
      .sort()
      .at(-1);
    expect(latestDate).toBeTruthy();

    render(createElement(Home));
    const outOfRangeDate = addOneDay(latestDate!);
    fireEvent.change(screen.getByLabelText("Fecha de subasta desde"), { target: { value: outOfRangeDate } });
    fireEvent.change(screen.getByLabelText("Fecha de subasta hasta"), { target: { value: outOfRangeDate } });

    expect(screen.getByText("No hay vehículos con esos filtros")).toBeTruthy();
  });

  it("applies a real LSC-base-budget range and reduces live results", () => {
    const highestBid = Math.max(...liveInventory.vehicles
      .map((vehicle) => vehicle.currentBidUsd)
      .filter((bid): bid is number => typeof bid === "number" && Number.isFinite(bid)));
    expect(Number.isFinite(highestBid)).toBe(true);

    render(createElement(Home));
    fireEvent.change(screen.getByLabelText("Presupuesto LSC mínimo"), { target: { value: String(highestBid + 700) } });

    expect(screen.getByText("No hay vehículos con esos filtros")).toBeTruthy();
  });
});
