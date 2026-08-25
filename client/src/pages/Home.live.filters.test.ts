// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { createElement } from "react";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";

type LiveVehicle = {
  lot: string;
  title: string | null;
  titleType: string | null;
  location: string | null;
  facilityId: string | null;
  state: string | null;
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

import Home, { auctionDateInEastern, buildFacilityLabel, isSpecialTitleType } from "./Home";

const addOneDay = (dateKey: string) => {
  const date = new Date(`${dateKey}T12:00:00Z`);
  date.setUTCDate(date.getUTCDate() + 1);
  return date.toISOString().slice(0, 10);
};

beforeAll(async () => {
  const response = await fetch("https://lsc-inv-revi-zyn4tlbw.manus.space/api/trpc/inventory.recent?input=%7B%22json%22%3A%7B%22take%22%3A1000%7D%7D");
  if (!response.ok) throw new Error(`Live inventory bridge returned ${response.status}`);
  const payload = await response.json() as { result?: { data?: { json?: typeof liveInventory } } };
  const parsed = payload.result?.data?.json;
  if (!parsed || parsed.vehicles.length === 0) throw new Error("Live inventory cut is empty");
  liveInventory = parsed;
}, 30_000);

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

  it("keeps live special-title lots hidden by default and reveals them explicitly", () => {
    const specialVehicle = liveInventory.vehicles.find((vehicle) => vehicle.titleType && isSpecialTitleType(vehicle.titleType));
    expect(specialVehicle?.titleType).toBeTruthy();

    render(createElement(Home));
    expect(screen.queryByText(specialVehicle!.title ?? `Lote ${specialVehicle!.lot}`)).toBeNull();

    fireEvent.click(screen.getByRole("checkbox", { name: new RegExp(specialVehicle!.titleType!, "i") }));
    expect(screen.getByText(specialVehicle!.title ?? `Lote ${specialVehicle!.lot}`)).toBeTruthy();
  });

  it("finds a live special-title lot through direct lot search", () => {
    const specialVehicle = liveInventory.vehicles.find((vehicle) => vehicle.titleType && isSpecialTitleType(vehicle.titleType));
    expect(specialVehicle).toBeTruthy();

    render(createElement(Home));
    fireEvent.change(screen.getByLabelText("Buscar vehículos"), { target: { value: specialVehicle!.lot } });
    expect(screen.getByText(specialVehicle!.title ?? `Lote ${specialVehicle!.lot}`)).toBeTruthy();
  });

  it("selects a real facility from the 14-facility cut and reduces visible results", () => {
    const facilityCounts = new Map<string, number>();
    for (const vehicle of liveInventory.vehicles) {
      if (!vehicle.location || !vehicle.facilityId || (vehicle.titleType && isSpecialTitleType(vehicle.titleType))) continue;
      const label = buildFacilityLabel(vehicle.location, vehicle.facilityId);
      if (label) facilityCounts.set(label, (facilityCounts.get(label) ?? 0) + 1);
    }
    const facility = [...facilityCounts.entries()].find(([, count]) => count > 0 && count < liveInventory.vehicles.length);
    expect(facility).toBeTruthy();

    render(createElement(Home));
    const countBefore = Number.parseInt(document.querySelector(".browse-results-head h2")?.textContent ?? "0", 10);
    fireEvent.click(screen.getByRole("checkbox", { name: new RegExp(`Facility ${facility![0].split("Facility ")[1]}`) }));
    const countAfter = Number.parseInt(document.querySelector(".browse-results-head h2")?.textContent ?? "0", 10);
    expect(countAfter).toBeGreaterThan(0);
    expect(countAfter).toBeLessThan(countBefore);
  });

  it("activates the live FL state control on the expanded cut", () => {
    render(createElement(Home));

    const countBefore = Number.parseInt(document.querySelector(".browse-results-head h2")?.textContent ?? "0", 10);
    const stateSection = screen.getByText("Estado", { selector: ".filter-group-label b" }).closest("section");
    const stateLabel = Array.from(stateSection?.querySelectorAll("label") ?? []).find((label) => label.querySelector("span")?.textContent === "FL");
    const stateFilter = stateLabel?.querySelector("input[type='checkbox']") as HTMLInputElement;
    expect(stateFilter).toBeTruthy();
    fireEvent.click(stateFilter);
    expect(stateFilter.checked).toBe(true);
    const countAfter = Number.parseInt(document.querySelector(".browse-results-head h2")?.textContent ?? "0", 10);
    expect(countAfter).toBeGreaterThan(0);
    expect(countAfter).toBeLessThan(countBefore);
  });

  it("navigates the live cut and resets to page one when sorting changes", () => {
    render(createElement(Home));
    const visibleTotal = Number.parseInt(document.querySelector(".browse-results-head h2")?.textContent ?? "0", 10);
    expect(visibleTotal).toBeGreaterThan(24);
    expect(document.querySelectorAll(".browse-row")).toHaveLength(24);

    fireEvent.click(screen.getByRole("button", { name: "Siguiente" }));
    expect(screen.getByText(`Mostrando 25–48 de ${visibleTotal}`)).toBeTruthy();

    fireEvent.change(screen.getByLabelText("Ordenar resultados"), { target: { value: "bid-low" } });
    expect(screen.getByText(`Mostrando 1–24 de ${visibleTotal}`)).toBeTruthy();
  });
});
