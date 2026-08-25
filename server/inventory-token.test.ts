import { createServer, type Server } from "node:http";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

let server: Server;
let baseUrl = "";

beforeAll(async () => {
  const expectedToken = process.env.INVENTORY_API_TOKEN;
  expect(expectedToken).toBeTruthy();

  server = createServer((request, response) => {
    const supplied = request.headers.authorization?.replace(/^Bearer\s+/i, "");
    if (supplied !== expectedToken) {
      response.writeHead(401).end();
      return;
    }
    response.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify({ status: "ready" }));
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  if (!address || typeof address === "string") throw new Error("Token test endpoint did not start");
  baseUrl = `http://127.0.0.1:${address.port}`;
});

afterAll(async () => {
  await new Promise<void>((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
});

describe("inventory API service token", () => {
  it("authenticates a lightweight read endpoint without exposing the token", async () => {
    const response = await fetch(`${baseUrl}/healthz`, {
      headers: { authorization: `Bearer ${process.env.INVENTORY_API_TOKEN!}` },
    });
    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({ status: "ready" });
  });
});

// The token is never included in logs, assertions, snapshots, or response bodies.
