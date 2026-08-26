import { chromium } from "playwright-core";

const browser = await chromium.launch({ headless: true, executablePath: "/usr/bin/chromium", args: ["--no-sandbox"] });
const searchUrl = "https://bid.cars/en/search/results?search-type=filters&status=All&type=Automobile&make=All&model=All&year-from=1900&year-to=2027&auction-type=All";
const detailUrl = "https://bid.cars/en/lot/0-45839677/1989-Jaguar-XJS-SAJNV4841KC157397";

const compact = (items) => [...new Set(items.map((item) => item.replace(/\s+/g, " ").trim()).filter(Boolean))].slice(0, 300);
const auditPage = async (page, url, name) => {
  await page.goto(url, { waitUntil: "domcontentloaded", timeout: 90_000 });
  await page.waitForTimeout(4_000);
  const result = await page.evaluate(() => {
    const text = (selector) => [...document.querySelectorAll(selector)].map((node) => node.textContent ?? "");
    const inputs = [...document.querySelectorAll("input")].map((input) => ({
      type: input.type,
      name: input.name,
      placeholder: input.placeholder,
      ariaLabel: input.getAttribute("aria-label"),
    }));
    return {
      title: document.title,
      url: location.href,
      headings: text("h1,h2,h3,h4"),
      labels: text("label,legend"),
      buttons: text("button,[role=button]"),
      links: text("a"),
      inputs,
      bodyText: document.body.innerText.slice(0, 30_000),
    };
  });
  return {
    name,
    title: result.title,
    url: result.url,
    headings: compact(result.headings),
    labels: compact(result.labels),
    buttons: compact(result.buttons),
    links: compact(result.links),
    inputs: result.inputs.slice(0, 150),
    bodyText: result.bodyText,
  };
};

const desktop = await browser.newPage({ viewport: { width: 1440, height: 1200 } });
const mobile = await browser.newPage({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });

const searchDesktop = await auditPage(desktop, searchUrl, "search-desktop");
const detailDesktop = await auditPage(desktop, detailUrl, "detail-desktop");
const searchMobile = await auditPage(mobile, searchUrl, "search-mobile");

await browser.close();
console.log(JSON.stringify({ generatedAt: new Date().toISOString(), pages: [searchDesktop, detailDesktop, searchMobile] }, null, 2));
