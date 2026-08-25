// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { createElement } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

const liveInventory = {
  source: "lsc-inventory-postgres",
  generatedAt: "2026-08-25T18:40:00.000Z",
  vehicles: [
    {
      lot: "11111111", observedAt: "2026-08-25T18:40:00.000Z", title: "2020 ALPHA SEDAN", year: 2020, make: "ALPHA", model: "SEDAN", vehicleType: "AUTOMOBILE", color: null,
      fuelType: "Gas", transmission: "Automatic", driveType: "FRONT WHEEL DRIVE", odometer: 20000, damage: "Front end", auctionAt: "2026-08-25T14:00:00.000Z", lotStatus: "open", currentBidUsd: 1000, buyNowUsd: null,
      location: "Clewiston (FL)", state: "FL", titleType: "CT", facilityId: "366", photos: [],
    },
    {
      lot: "22222222", observedAt: "2026-08-25T18:40:00.000Z", title: "2021 BETA SUV", year: 2021, make: "BETA", model: "SUV", vehicleType: "AUTOMOBILE", color: null,
      fuelType: "Gas", transmission: "Automatic", driveType: "ALL WHEEL DRIVE", odometer: 30000, damage: "Side", auctionAt: "2026-08-26T14:00:00.000Z", lotStatus: "open", currentBidUsd: 5000, buyNowUsd: null,
      location: "Clewiston (FL)", state: "FL", titleType: "CT", facilityId: "366", photos: [],
    },
    {
      lot: "33333333", observedAt: "2026-08-25T18:40:00.000Z", title: "2019 SPECIAL PARTS CAR", year: 2019, make: "SPECIAL", model: "PARTS CAR", vehicleType: "AUTOMOBILE", color: null,
      fuelType: "Gas", transmission: "Automatic", driveType: "FRONT WHEEL DRIVE", odometer: 40000, damage: "Front end", auctionAt: "2026-08-25T14:00:00.000Z", lotStatus: "open", currentBidUsd: 500, buyNowUsd: null,
      location: "Clewiston (FL)", state: "FL", titleType: "CERTIFICATE OF DESTRUCTION", facilityId: "366", photos: [],
    },
  ],
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

import Home from "./Home";

afterEach(() => cleanup());

describe("Home live range filters", () => {
  it("reduces results when a Miami auction-date range is selected", () => {
    render(createElement(Home));

    fireEvent.change(screen.getByLabelText("Fecha de subasta desde"), { target: { value: "2026-08-25" } });
    fireEvent.change(screen.getByLabelText("Fecha de subasta hasta"), { target: { value: "2026-08-25" } });

    expect(screen.getByText("2020 ALPHA SEDAN")).toBeTruthy();
    expect(screen.queryByText("2021 BETA SUV")).toBeNull();
    expect(screen.getByRole("heading", { name: "1 vehículos" })).toBeTruthy();
  });

  it("reduces results when a disclosed LSC-base-budget range is selected", () => {
    render(createElement(Home));

    fireEvent.change(screen.getByLabelText("Presupuesto LSC mínimo"), { target: { value: "1300" } });
    fireEvent.change(screen.getByLabelText("Presupuesto LSC máximo"), { target: { value: "1700" } });

    expect(screen.getByText("2020 ALPHA SEDAN")).toBeTruthy();
    expect(screen.queryByText("2021 BETA SUV")).toBeNull();
    expect(screen.getByText("$1,399 – $1,699")).toBeTruthy();
  });

  it("keeps special titles stored but hidden until their title filter is selected", () => {
    render(createElement(Home));

    expect(screen.queryByText("2019 SPECIAL PARTS CAR")).toBeNull();
    const specialTitleFilter = screen.getByRole("checkbox", { name: /CERTIFICATE OF DESTRUCTION/ }) as HTMLInputElement;
    expect(specialTitleFilter.checked).toBe(false);

    fireEvent.click(specialTitleFilter);
    expect(screen.getByText("2019 SPECIAL PARTS CAR")).toBeTruthy();
  });

  it("allows a direct lot search to find a special-title vehicle", () => {
    render(createElement(Home));
    fireEvent.change(screen.getByLabelText("Buscar vehículos"), { target: { value: "33333333" } });
    expect(screen.getByText("2019 SPECIAL PARTS CAR")).toBeTruthy();
  });
});
