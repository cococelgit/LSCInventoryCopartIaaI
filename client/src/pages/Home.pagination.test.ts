// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { createElement } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

const vehicles = Array.from({ length: 30 }, (_, index) => ({
  lot: String(70000000 + index), observedAt: "2026-08-25T18:40:00.000Z", title: `2020 TEST CAR ${index + 1}`, year: 2020, make: "TEST", model: `CAR ${index + 1}`, vehicleType: "AUTOMOBILE", color: null,
  fuelType: "Gas", transmission: "Automatic", driveType: "FRONT WHEEL DRIVE", odometer: 20000 + index, damage: "Front end", auctionAt: `2026-08-${String(1 + (index % 20)).padStart(2, "0")}T14:00:00.000Z`, lotStatus: "open", currentBidUsd: 1000 + index, buyNowUsd: null,
  location: "Clewiston (FL)", state: "FL", titleType: "CT", facilityId: "366", photos: [],
}));

vi.mock("../lib/trpc", () => ({ trpc: { inventory: { recent: { useQuery: () => ({ data: { source: "test", generatedAt: "2026-08-25T18:40:00.000Z", vehicles }, isLoading: false }) } } } }));

import Home from "./Home";

afterEach(() => cleanup());

describe("Home pagination", () => {
  it("renders 24 vehicles first and navigates to the remaining page", () => {
    render(createElement(Home));
    expect(document.querySelectorAll(".browse-row")).toHaveLength(24);
    expect(screen.getByText("Mostrando 1–24 de 30")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Siguiente" }));

    expect(document.querySelectorAll(".browse-row")).toHaveLength(6);
    expect(screen.getByText("Mostrando 25–30 de 30")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Anterior" })).not.toHaveProperty("disabled", true);
  });
});
