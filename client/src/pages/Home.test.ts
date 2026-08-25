import { describe, expect, it } from "vitest";
import { buildOptionCounts, parseOdometer } from "./Home";

describe("buildOptionCounts", () => {
  it("groups and alphabetizes values supplied by the inventory feed", () => {
    expect(buildOptionCounts(["Salvage", "No reportado", "Salvage", "Clean"]))
      .toEqual([["Clean", 1], ["No reportado", 1], ["Salvage", 2]]);
  });
});

describe("parseOdometer", () => {
  it("accepts numeric values and rejects non-reported mileage", () => {
    expect(parseOdometer("64,250 mi")).toBe(64250);
    expect(parseOdometer(1240)).toBe(1240);
    expect(parseOdometer("No reportado")).toBeNull();
  });
});
