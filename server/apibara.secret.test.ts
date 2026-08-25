import { describe, expect, it } from "vitest";

describe("Apibara server secret", () => {
  it("authenticates against the lightweight usage endpoint without exposing the key", async () => {
    const apiKey = process.env.APIBARA_API_KEY;
    expect(apiKey).toBeTruthy();

    const response = await fetch("https://apibara.tech/api/v1/vehicle-auction/usage", {
      headers: { "X-API-Key": apiKey! },
      signal: AbortSignal.timeout(15_000),
    });

    expect(response.ok).toBe(true);
    await response.body?.cancel();
  }, 20_000);
});

// Never log or include the secret in assertions, errors, or snapshots.
