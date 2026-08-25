import { describe, expect, it } from "vitest";
import { buildOptionCounts } from "./Home";

describe("buildOptionCounts", () => {
  it("groups and alphabetizes values supplied by the inventory feed", () => {
    expect(buildOptionCounts(["Salvage", "No reportado", "Salvage", "Clean"]))
      .toEqual([["Clean", 1], ["No reportado", 1], ["Salvage", 2]]);
  });
});
