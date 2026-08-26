/** Inventario operativo: búsqueda y comparación rápidas, sin elementos decorativos que resten espacio a los lotes. */
import React, { useEffect, useMemo, useState } from "react";
import { trpc } from "../lib/trpc";
import { CalendarDays, Check, ChevronDown, ChevronUp, CircleAlert, Filter, Image, MapPin, Search, ShieldX, SlidersHorizontal, Tag, X } from "lucide-react";
import { formatMoney } from "../data/inventory";
import { LSC_BROKER_FEE_MAX_USD, LSC_BROKER_FEE_MIN_USD } from "../data/lscPricing";
import VehiclePhotoCarousel from "../components/VehiclePhotoCarousel";
import "./home-filters.css";

type SortMode = "auction" | "auction-desc" | "estimate-low" | "estimate-high" | "bid-low" | "bid-high" | "buy-low" | "buy-high" | "year-low" | "year-high" | "odometer-low" | "odometer-high";
type SourceMode = "all" | "copart" | "iaai";
type AuctionStatusMode = "all" | "open" | "live" | "finished" | "buy-now";
export const INVENTORY_PAGE_SIZE = 24;
const AUCTION_STATUS_TABS: ReadonlyArray<readonly [AuctionStatusMode, string]> = [["all", "Todos"], ["open", "Abierta"], ["live", "En vivo"], ["finished", "Finalizada"], ["buy-now", "Buy Now"]];

export { LSC_BROKER_FEE_MAX_USD, LSC_BROKER_FEE_MIN_USD } from "../data/lscPricing";

export function buildOptionCounts(values: string[]) {
  return Array.from(values.reduce((counts, value) => {
    counts.set(value, (counts.get(value) ?? 0) + 1);
    return counts;
  }, new Map<string, number>()).entries()).sort(([left], [right]) => left.localeCompare(right));
}

export function parseOdometer(value: unknown) {
  if (typeof value === "number" && Number.isFinite(value)) return value;
  if (typeof value !== "string") return null;
  const digits = value.replace(/[^0-9]/g, "");
  return digits ? Number(digits) : null;
}

export function buildFacilityLabel(location: string, facilityId: string | null) {
  if (!location || location === "No reportada") return null;
  return facilityId ? `${location} · Facility ${facilityId}` : location;
}

export function estimateBasePurchaseTotal(currentBid: number | null) {
  if (currentBid === null || !Number.isFinite(currentBid) || currentBid < 0) return null;
  return {
    min: currentBid + LSC_BROKER_FEE_MIN_USD,
    max: currentBid + LSC_BROKER_FEE_MAX_USD,
  };
}

export function auctionDateInEastern(auctionAt: string | null) {
  if (!auctionAt) return null;
  const instant = new Date(auctionAt);
  if (Number.isNaN(instant.getTime())) return null;
  const values = new Intl.DateTimeFormat("en-US", {
    timeZone: "America/New_York",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).formatToParts(instant).reduce<Record<string, string>>((parts, part) => ({ ...parts, [part.type]: part.value }), {});
  return values.year && values.month && values.day ? `${values.year}-${values.month}-${values.day}` : null;
}

export function isAuctionDateInRange(auctionDate: string | null, from: string, to: string) {
  if (!from && !to) return true;
  return auctionDate !== null && auctionDate >= (from || "0000-01-01") && auctionDate <= (to || "9999-12-31");
}

export function doesEstimatedTotalOverlap(estimate: ReturnType<typeof estimateBasePurchaseTotal>, minimum: string, maximum: string) {
  if (!minimum && !maximum) return true;
  return estimate !== null && estimate.max >= Number(minimum || 0) && estimate.min <= Number(maximum || Number.MAX_SAFE_INTEGER);
}

export function numberInRange(value: number | null, minimum: string, maximum: string) {
  if (!minimum && !maximum) return true;
  return value !== null && value >= Number(minimum || Number.MIN_SAFE_INTEGER) && value <= Number(maximum || Number.MAX_SAFE_INTEGER);
}

const SPECIAL_TITLE_PHRASES = ["CERTIFICATE OF DESTRUCTION", "JUNK", "NON REPAIRABLE", "PARTS ONLY"];

export function isSpecialTitleType(titleType: string) {
  const normalized = titleType.toUpperCase().replace(/[-/_,.]+/g, " ").replace(/\s+/g, " ").trim();
  return SPECIAL_TITLE_PHRASES.some((phrase) => ` ${normalized} `.includes(` ${phrase} `));
}

export function buildPaginationPages(currentPage: number, totalPages: number) {
  const pages = new Set([1, totalPages, currentPage - 1, currentPage, currentPage + 1]);
  return Array.from(pages).filter((page) => page >= 1 && page <= totalPages).sort((left, right) => left - right);
}

export default function Home() {
  const [query, setQuery] = useState("");
  const [sourceMode, setSourceMode] = useState<SourceMode>("all");
  const [auctionStatusMode, setAuctionStatusMode] = useState<AuctionStatusMode>("all");
  const [selectedMakes, setSelectedMakes] = useState<string[]>([]);
  const [minYear, setMinYear] = useState("2000");
  const [maxYear, setMaxYear] = useState("2026");
  const [maxBid, setMaxBid] = useState("25000");
  const [onlyBid, setOnlyBid] = useState(false);
  const [onlyPhotos, setOnlyPhotos] = useState(false);
  const [selectedDamages, setSelectedDamages] = useState<string[]>([]);
  const [selectedTitles, setSelectedTitles] = useState<string[] | null>(null);
  const [selectedTransmissions, setSelectedTransmissions] = useState<string[]>([]);
  const [selectedModels, setSelectedModels] = useState<string[]>([]);
  const [selectedVehicleTypes, setSelectedVehicleTypes] = useState<string[]>([]);
  const [selectedStartCodes, setSelectedStartCodes] = useState<string[]>([]);
  const [selectedDrives, setSelectedDrives] = useState<string[]>([]);
  const [selectedFuels, setSelectedFuels] = useState<string[]>([]);
  const [selectedFacilities, setSelectedFacilities] = useState<string[]>([]);
  const [selectedStates, setSelectedStates] = useState<string[]>([]);
  const [selectedBodyStyles, setSelectedBodyStyles] = useState<string[]>([]);
  const [selectedColors, setSelectedColors] = useState<string[]>([]);
  const [selectedLossTypes, setSelectedLossTypes] = useState<string[]>([]);
  const [selectedEngineLayouts, setSelectedEngineLayouts] = useState<string[]>([]);
  const [selectedCylinders, setSelectedCylinders] = useState<string[]>([]);
  const [selectedSellerTypes, setSelectedSellerTypes] = useState<string[]>([]);
  const [keyMode, setKeyMode] = useState<"all" | "with" | "without">("all");
  const [auctionFrom, setAuctionFrom] = useState("");
  const [auctionTo, setAuctionTo] = useState("");
  const [minEstimatedTotal, setMinEstimatedTotal] = useState("");
  const [maxEstimatedTotal, setMaxEstimatedTotal] = useState("");
  const [minOdometer, setMinOdometer] = useState("");
  const [maxOdometer, setMaxOdometer] = useState("");
  const [minProviderEstimate, setMinProviderEstimate] = useState("");
  const [maxProviderEstimate, setMaxProviderEstimate] = useState("");
  const [minEngineSize, setMinEngineSize] = useState("");
  const [maxEngineSize, setMaxEngineSize] = useState("");
  const [minHorsepower, setMinHorsepower] = useState("");
  const [maxHorsepower, setMaxHorsepower] = useState("");
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [sortMode, setSortMode] = useState<SortMode>("auction");
  const [currentPage, setCurrentPage] = useState(1);
  const liveInventory = trpc.inventory.recent.useQuery({ take: 1000 }, { refetchOnWindowFocus: true, staleTime: 60_000 });

  const vehicles = useMemo(() => liveInventory.data?.vehicles?.map((vehicle) => {
    const location = vehicle.location ?? "No reportada";
    const facilityId = vehicle.facilityId ?? null;
    return {
      lot: vehicle.lot,
      platform: (vehicle.platform ?? "unknown").toLowerCase(),
      title: vehicle.title ?? `Lote ${vehicle.lot}`,
      year: vehicle.year ?? 0,
      make: vehicle.make ?? "Sin marca",
      model: vehicle.model ?? "Sin modelo",
      series: vehicle.series ?? null,
      bodyStyle: vehicle.bodyStyle ?? "No reportado",
      color: vehicle.color ?? "No reportado",
      currentBid: vehicle.currentBidUsd,
      preBid: vehicle.preBidUsd ?? null,
      buyNow: vehicle.buyNowUsd ?? null,
      providerEstimateMin: vehicle.estimatedPriceFromUsd ?? null,
      providerEstimateMax: vehicle.estimatedPriceToUsd ?? null,
      estimatedTotal: estimateBasePurchaseTotal(vehicle.currentBidUsd),
      photos: vehicle.photos,
      auctionAt: vehicle.auctionAt,
      auctionDateKey: auctionDateInEastern(vehicle.auctionAt),
      auctionDate: vehicle.auctionAt ? new Date(vehicle.auctionAt).toLocaleDateString("es-US", { day: "2-digit", month: "short", year: "numeric" }).toUpperCase() : "No reportada",
      location,
      facilityId,
      facilityLabel: buildFacilityLabel(location, facilityId),
      state: vehicle.state ?? "No reportado",
      damage: vehicle.damage ?? "No reportado",
      secondaryDamage: vehicle.secondaryDamage ?? "No reportado",
      lossType: vehicle.lossType ?? "No reportado",
      transmission: vehicle.transmission ?? "No reportada",
      fuel: vehicle.fuelType ?? "No reportado",
      drive: vehicle.driveType ?? "No reportada",
      titleType: vehicle.titleType ?? "No reportado",
      vehicleType: vehicle.vehicleType ?? "No reportado",
      startCode: vehicle.startCode ?? "No reportado",
      hasKey: vehicle.hasKey ?? null,
      sellerName: vehicle.sellerName ?? "No reportado",
      sellerType: vehicle.sellerType ?? "No reportado",
      engineSize: vehicle.engineSizeLiters ? Number.parseFloat(vehicle.engineSizeLiters) : null,
      engineLayout: vehicle.engineLayout ?? "No reportado",
      engineDescription: vehicle.engineDescription ?? null,
      horsepower: vehicle.engineHorsepower ?? null,
      cylinders: vehicle.cylinders ?? "No reportado",
      odometer: parseOdometer(vehicle.odometer),
      odometerKm: parseOdometer(vehicle.odometerKm),
      status: vehicle.lotStatus || "En seguimiento",
      lotSubStatus: vehicle.lotSubStatus ?? "",
      isBuyNow: vehicle.isBuyNow === true || vehicle.buyNowUsd !== null,
      has360: vehicle.has360 === true,
      hasVideo: vehicle.hasVideo === true,
    };
  }) ?? [], [liveInventory.data]);

  const makes = useMemo(() => Array.from(new Set(vehicles.map((vehicle) => vehicle.make))).sort(), [vehicles]);
  const sourceCounts = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.platform)), [vehicles]);
  const models = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.model)), [vehicles]);
  const vehicleTypes = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.vehicleType)), [vehicles]);
  const damages = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.damage)), [vehicles]);
  const titleTypes = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.titleType)), [vehicles]);
  const defaultVisibleTitles = useMemo(() => titleTypes.map(([titleType]) => titleType).filter((titleType) => !isSpecialTitleType(titleType)), [titleTypes]);
  const activeTitleTypes = selectedTitles ?? defaultVisibleTitles;
  const transmissions = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.transmission)), [vehicles]);
  const startCodes = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.startCode)), [vehicles]);
  const drives = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.drive)), [vehicles]);
  const fuels = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.fuel)), [vehicles]);
  const facilities = useMemo(() => buildOptionCounts(vehicles.flatMap((vehicle) => vehicle.facilityLabel ? [vehicle.facilityLabel] : [])), [vehicles]);
  const states = useMemo(() => buildOptionCounts(vehicles.flatMap((vehicle) => vehicle.state !== "No reportado" ? [vehicle.state] : [])), [vehicles]);
  const bodyStyles = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.bodyStyle)), [vehicles]);
  const colors = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.color)), [vehicles]);
  const lossTypes = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.lossType)), [vehicles]);
  const engineLayouts = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.engineLayout)), [vehicles]);
  const cylinderOptions = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.cylinders)), [vehicles]);
  const sellerTypes = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.sellerType)), [vehicles]);
  const filteredVehicles = useMemo(() => vehicles.filter((vehicle) => {
    const normalizedQuery = query.trim().toLowerCase();
    const normalized = `${vehicle.title} ${vehicle.lot} ${vehicle.make} ${vehicle.model} ${vehicle.titleType}`.toLowerCase();
    const directSpecialTitleSearch = normalizedQuery.length > 0 && (vehicle.lot.toLowerCase() === normalizedQuery || vehicle.titleType.toLowerCase().includes(normalizedQuery));
    const appliesOdometer = Boolean(minOdometer || maxOdometer);
    const appliesAuctionDate = Boolean(auctionFrom || auctionTo);
    const appliesEstimatedTotal = Boolean(minEstimatedTotal || maxEstimatedTotal);
    const statusText = `${vehicle.status} ${vehicle.lotSubStatus}`.toLowerCase();
    const matchesAuctionStatus = auctionStatusMode === "all"
      || (auctionStatusMode === "open" && (statusText.includes("open") || statusText.includes("active")))
      || (auctionStatusMode === "live" && statusText.includes("live"))
      || (auctionStatusMode === "finished" && (statusText.includes("finished") || statusText.includes("ended") || statusText.includes("sold")))
      || (auctionStatusMode === "buy-now" && vehicle.isBuyNow);
    return normalized.includes(normalizedQuery)
      && (sourceMode === "all" || vehicle.platform === sourceMode)
      && matchesAuctionStatus
      && (selectedMakes.length === 0 || selectedMakes.includes(vehicle.make))
      && (selectedModels.length === 0 || selectedModels.includes(vehicle.model))
      && (selectedFacilities.length === 0 || (vehicle.facilityLabel !== null && selectedFacilities.includes(vehicle.facilityLabel)))
      && (selectedStates.length === 0 || selectedStates.includes(vehicle.state))
      && vehicle.year >= Number(minYear || 0)
      && vehicle.year <= Number(maxYear || 9999)
      && (!appliesAuctionDate || isAuctionDateInRange(vehicle.auctionDateKey, auctionFrom, auctionTo))
      && (!appliesOdometer || (vehicle.odometer !== null && vehicle.odometer >= Number(minOdometer || 0) && vehicle.odometer <= Number(maxOdometer || Number.MAX_SAFE_INTEGER)))
      && (!appliesEstimatedTotal || doesEstimatedTotalOverlap(vehicle.estimatedTotal, minEstimatedTotal, maxEstimatedTotal))
      && (!minProviderEstimate && !maxProviderEstimate || (vehicle.providerEstimateMin !== null && vehicle.providerEstimateMax !== null && vehicle.providerEstimateMax >= Number(minProviderEstimate || 0) && vehicle.providerEstimateMin <= Number(maxProviderEstimate || Number.MAX_SAFE_INTEGER)))
      && numberInRange(vehicle.engineSize, minEngineSize, maxEngineSize)
      && numberInRange(vehicle.horsepower, minHorsepower, maxHorsepower)
      && (vehicle.currentBid === null || vehicle.currentBid <= Number(maxBid || Infinity))
      && (!onlyBid || vehicle.currentBid !== null)
      && (!onlyPhotos || vehicle.photos.length > 0)
      && (selectedVehicleTypes.length === 0 || selectedVehicleTypes.includes(vehicle.vehicleType))
      && (selectedDamages.length === 0 || selectedDamages.includes(vehicle.damage))
      && (directSpecialTitleSearch || activeTitleTypes.includes(vehicle.titleType))
      && (selectedStartCodes.length === 0 || selectedStartCodes.includes(vehicle.startCode))
      && (selectedDrives.length === 0 || selectedDrives.includes(vehicle.drive))
      && (selectedTransmissions.length === 0 || selectedTransmissions.includes(vehicle.transmission))
      && (selectedFuels.length === 0 || selectedFuels.includes(vehicle.fuel))
      && (selectedBodyStyles.length === 0 || selectedBodyStyles.includes(vehicle.bodyStyle))
      && (selectedColors.length === 0 || selectedColors.includes(vehicle.color))
      && (selectedLossTypes.length === 0 || selectedLossTypes.includes(vehicle.lossType))
      && (selectedEngineLayouts.length === 0 || selectedEngineLayouts.includes(vehicle.engineLayout))
      && (selectedCylinders.length === 0 || selectedCylinders.includes(vehicle.cylinders))
      && (selectedSellerTypes.length === 0 || selectedSellerTypes.includes(vehicle.sellerType))
      && (keyMode === "all" || (keyMode === "with" ? vehicle.hasKey === true : vehicle.hasKey === false));
  }), [vehicles, sourceMode, auctionStatusMode, query, selectedMakes, selectedModels, selectedFacilities, selectedStates, minYear, maxYear, auctionFrom, auctionTo, minOdometer, maxOdometer, minEstimatedTotal, maxEstimatedTotal, minProviderEstimate, maxProviderEstimate, minEngineSize, maxEngineSize, minHorsepower, maxHorsepower, maxBid, onlyBid, onlyPhotos, selectedVehicleTypes, selectedDamages, activeTitleTypes, selectedStartCodes, selectedDrives, selectedTransmissions, selectedFuels, selectedBodyStyles, selectedColors, selectedLossTypes, selectedEngineLayouts, selectedCylinders, selectedSellerTypes, keyMode]);
  const results = useMemo(() => [...filteredVehicles].sort((left, right) => {
    if (sortMode === "auction-desc") return (new Date(right.auctionAt ?? "1900-01-01").getTime()) - (new Date(left.auctionAt ?? "1900-01-01").getTime());
    if (sortMode === "estimate-low") return (left.providerEstimateMin ?? Number.MAX_SAFE_INTEGER) - (right.providerEstimateMin ?? Number.MAX_SAFE_INTEGER);
    if (sortMode === "estimate-high") return (right.providerEstimateMax ?? -1) - (left.providerEstimateMax ?? -1);
    if (sortMode === "bid-low") return (left.currentBid ?? Number.MAX_SAFE_INTEGER) - (right.currentBid ?? Number.MAX_SAFE_INTEGER);
    if (sortMode === "bid-high") return (right.currentBid ?? -1) - (left.currentBid ?? -1);
    if (sortMode === "buy-low") return (left.buyNow ?? Number.MAX_SAFE_INTEGER) - (right.buyNow ?? Number.MAX_SAFE_INTEGER);
    if (sortMode === "buy-high") return (right.buyNow ?? -1) - (left.buyNow ?? -1);
    if (sortMode === "year-low") return left.year - right.year;
    if (sortMode === "year-high") return right.year - left.year;
    if (sortMode === "odometer-low") return (left.odometer ?? Number.MAX_SAFE_INTEGER) - (right.odometer ?? Number.MAX_SAFE_INTEGER);
    if (sortMode === "odometer-high") return (right.odometer ?? -1) - (left.odometer ?? -1);
    return (new Date(left.auctionAt ?? "2999-12-31").getTime()) - (new Date(right.auctionAt ?? "2999-12-31").getTime());
  }), [filteredVehicles, sortMode]);
  const totalPages = Math.max(1, Math.ceil(results.length / INVENTORY_PAGE_SIZE));
  const paginationPages = useMemo(() => buildPaginationPages(currentPage, totalPages), [currentPage, totalPages]);
  const paginatedResults = useMemo(() => results.slice((currentPage - 1) * INVENTORY_PAGE_SIZE, currentPage * INVENTORY_PAGE_SIZE), [results, currentPage]);
  const filterSignature = JSON.stringify({ sourceMode, auctionStatusMode, query, selectedMakes, minYear, maxYear, maxBid, onlyBid, onlyPhotos, selectedDamages, activeTitleTypes, selectedTransmissions, selectedModels, selectedVehicleTypes, selectedStartCodes, selectedDrives, selectedFuels, selectedFacilities, selectedStates, selectedBodyStyles, selectedColors, selectedLossTypes, selectedEngineLayouts, selectedCylinders, selectedSellerTypes, keyMode, auctionFrom, auctionTo, minEstimatedTotal, maxEstimatedTotal, minProviderEstimate, maxProviderEstimate, minOdometer, maxOdometer, minEngineSize, maxEngineSize, minHorsepower, maxHorsepower, sortMode });

  useEffect(() => setCurrentPage(1), [filterSignature]);
  useEffect(() => setCurrentPage((page) => Math.min(page, totalPages)), [totalPages]);

  const toggleMake = (make: string) => setSelectedMakes((active) => active.includes(make) ? active.filter((item) => item !== make) : [...active, make]);
  const toggleValue = (value: string, active: string[], update: (next: string[]) => void) => update(active.includes(value) ? active.filter((item) => item !== value) : [...active, value]);
  const clearFilters = () => { setSourceMode("all"); setAuctionStatusMode("all"); setQuery(""); setSelectedMakes([]); setSelectedModels([]); setSelectedVehicleTypes([]); setSelectedDamages([]); setSelectedTitles(null); setSelectedStartCodes([]); setSelectedDrives([]); setSelectedTransmissions([]); setSelectedFuels([]); setSelectedFacilities([]); setSelectedStates([]); setSelectedBodyStyles([]); setSelectedColors([]); setSelectedLossTypes([]); setSelectedEngineLayouts([]); setSelectedCylinders([]); setSelectedSellerTypes([]); setKeyMode("all"); setMinYear("2000"); setMaxYear("2027"); setAuctionFrom(""); setAuctionTo(""); setMinOdometer(""); setMaxOdometer(""); setMinEstimatedTotal(""); setMaxEstimatedTotal(""); setMinProviderEstimate(""); setMaxProviderEstimate(""); setMinEngineSize(""); setMaxEngineSize(""); setMinHorsepower(""); setMaxHorsepower(""); setMaxBid("25000"); setOnlyBid(false); setOnlyPhotos(false); };
  const handleAuctionStatusKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>, index: number) => {
    if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
    event.preventDefault();
    const nextIndex = event.key === "Home" ? 0 : event.key === "End" ? AUCTION_STATUS_TABS.length - 1 : event.key === "ArrowRight" ? (index + 1) % AUCTION_STATUS_TABS.length : (index - 1 + AUCTION_STATUS_TABS.length) % AUCTION_STATUS_TABS.length;
    setAuctionStatusMode(AUCTION_STATUS_TABS[nextIndex][0]);
    event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>("[role='tab']")[nextIndex]?.focus();
  };

  return <main className="browse-page">
    <header className="browse-header">
      <a className="browse-brand" href="/" aria-label="La Subasta Cubana"><img src="/manus-storage/lsc-logo-lineal-blanco_435d949d.png" alt="La Subasta Cubana" /></a>
      <div className="browse-search" role="search"><span className="browse-scope">Inventario <ChevronDown size={14} /></span><Search size={19} /><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Busca por marca, modelo o número de lote" aria-label="Buscar vehículos" /></div>
      <div className="browse-header-meta"><span><i /> {sourceMode === "all" ? "COPART + IAAI" : sourceMode.toUpperCase()}</span><small>{vehicles.length} lotes</small></div>
      <a className="browse-internal-link" href="/interno/descartes"><ShieldX size={15} /> Auditoría</a>
      <button className="browse-filter-button" onClick={() => setFiltersOpen(true)}><SlidersHorizontal size={18} /> Filtros</button>
    </header>

    <div className="browse-shell">
      <aside className={`browse-sidebar ${filtersOpen ? "browse-sidebar--open" : ""}`}>
        <div className="sidebar-top"><div><p>FILTROS</p><h1>Busca tu carro</h1></div><button onClick={() => setFiltersOpen(false)} aria-label="Cerrar filtros"><X size={18} /></button></div>
        <div className="sidebar-scroll">
          <section className="filter-group">
            <div className="filter-group-label"><b>Fuente</b><span>Disponible</span></div>
            <div className="source-pills"><button className={sourceMode === "all" ? "source-pill source-pill--active" : "source-pill"} onClick={() => setSourceMode("all")}>Todos</button><button className={sourceMode === "copart" ? "source-pill source-pill--active" : "source-pill"} onClick={() => setSourceMode("copart")}>Copart · {sourceCounts.find(([source]) => source === "copart")?.[1] ?? 0}</button><button className={sourceMode === "iaai" ? "source-pill source-pill--active" : "source-pill"} onClick={() => setSourceMode("iaai")}>IAAI · {sourceCounts.find(([source]) => source === "iaai")?.[1] ?? 0}</button></div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Precio estimado</b><span>USD · Proveedor</span></div>
            <div className="browse-range"><input value={minProviderEstimate} onChange={(event) => setMinProviderEstimate(event.target.value)} inputMode="numeric" aria-label="Precio estimado mínimo" placeholder="Desde" /><span>—</span><input value={maxProviderEstimate} onChange={(event) => setMaxProviderEstimate(event.target.value)} inputMode="numeric" aria-label="Precio estimado máximo" placeholder="Hasta" /></div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Ubicación / facility</b><button onClick={() => setSelectedFacilities([])}>Limpiar</button></div>
            {facilities.length > 0 ? <div className="make-list">{facilities.map(([facility, count]) => <label key={facility}><input type="checkbox" checked={selectedFacilities.includes(facility)} onChange={() => toggleValue(facility, selectedFacilities, setSelectedFacilities)} /><i><Check size={11} /></i><span>{facility}</span><small>{count}</small></label>)}</div> : <p className="filter-empty">El feed aún no reporta facility.</p>}
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Estado</b><button onClick={() => setSelectedStates([])}>Limpiar</button></div>
            {states.length > 0 ? <div className="make-list">{states.map(([state, count]) => <label key={state}><input type="checkbox" checked={selectedStates.includes(state)} onChange={() => toggleValue(state, selectedStates, setSelectedStates)} /><i><Check size={11} /></i><span>{state}</span><small>{count}</small></label>)}</div> : <p className="filter-empty">El feed aún no reporta estado.</p>}
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Año</b><span>Rango</span></div>
            <div className="browse-range"><input value={minYear} onChange={(event) => setMinYear(event.target.value)} inputMode="numeric" aria-label="Año mínimo" /><span>—</span><input value={maxYear} onChange={(event) => setMaxYear(event.target.value)} inputMode="numeric" aria-label="Año máximo" /></div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Fecha de subasta</b><span>Hora de Miami</span></div>
            <div className="browse-range"><input type="date" value={auctionFrom} onChange={(event) => setAuctionFrom(event.target.value)} aria-label="Fecha de subasta desde" /><span>—</span><input type="date" value={auctionTo} onChange={(event) => setAuctionTo(event.target.value)} aria-label="Fecha de subasta hasta" /></div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Odómetro</b><span>Millas</span></div>
            <div className="browse-range"><input value={minOdometer} onChange={(event) => setMinOdometer(event.target.value)} inputMode="numeric" aria-label="Odómetro mínimo" placeholder="Desde" /><span>—</span><input value={maxOdometer} onChange={(event) => setMaxOdometer(event.target.value)} inputMode="numeric" aria-label="Odómetro máximo" placeholder="Hasta" /></div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Presupuesto LSC base</b><span>USD</span></div>
            <div className="browse-range"><input value={minEstimatedTotal} onChange={(event) => setMinEstimatedTotal(event.target.value)} inputMode="numeric" aria-label="Presupuesto LSC mínimo" placeholder="Desde" /><span>—</span><input value={maxEstimatedTotal} onChange={(event) => setMaxEstimatedTotal(event.target.value)} inputMode="numeric" aria-label="Presupuesto LSC máximo" placeholder="Hasta" /></div>
            <p className="filter-hint">Puja actual + broker fee LSC de $399–$699. El rango muestra resultados que se crucen con tu presupuesto.</p>
          </section>
          <section className="filter-group filter-group--makes">
            <div className="filter-group-label"><b>Marca</b><button onClick={() => setSelectedMakes([])}>Limpiar</button></div>
            <div className="make-list">{makes.map((make) => <label key={make}><input type="checkbox" checked={selectedMakes.includes(make)} onChange={() => toggleMake(make)} /><i><Check size={11} /></i><span>{make}</span><small>{vehicles.filter((vehicle) => vehicle.make === make).length}</small></label>)}</div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Modelo</b><button onClick={() => setSelectedModels([])}>Limpiar</button></div>
            <div className="make-list">{models.map(([model, count]) => <label key={model}><input type="checkbox" checked={selectedModels.includes(model)} onChange={() => toggleValue(model, selectedModels, setSelectedModels)} /><i><Check size={11} /></i><span>{model}</span><small>{count}</small></label>)}</div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Tipo de vehículo</b><button onClick={() => setSelectedVehicleTypes([])}>Limpiar</button></div>
            <div className="make-list">{vehicleTypes.map(([vehicleType, count]) => <label key={vehicleType}><input type="checkbox" checked={selectedVehicleTypes.includes(vehicleType)} onChange={() => toggleValue(vehicleType, selectedVehicleTypes, setSelectedVehicleTypes)} /><i><Check size={11} /></i><span>{vehicleType}</span><small>{count}</small></label>)}</div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Body style</b><button onClick={() => setSelectedBodyStyles([])}>Limpiar</button></div>
            <div className="make-list">{bodyStyles.map(([bodyStyle, count]) => <label key={bodyStyle}><input type="checkbox" checked={selectedBodyStyles.includes(bodyStyle)} onChange={() => toggleValue(bodyStyle, selectedBodyStyles, setSelectedBodyStyles)} /><i><Check size={11} /></i><span>{bodyStyle}</span><small>{count}</small></label>)}</div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Tipo de daño</b><button onClick={() => setSelectedDamages([])}>Limpiar</button></div>
            <div className="make-list">{damages.map(([damage, count]) => <label key={damage}><input type="checkbox" checked={selectedDamages.includes(damage)} onChange={() => toggleValue(damage, selectedDamages, setSelectedDamages)} /><i><Check size={11} /></i><span>{damage}</span><small>{count}</small></label>)}</div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Loss type</b><button onClick={() => setSelectedLossTypes([])}>Limpiar</button></div>
            <div className="make-list">{lossTypes.map(([lossType, count]) => <label key={lossType}><input type="checkbox" checked={selectedLossTypes.includes(lossType)} onChange={() => toggleValue(lossType, selectedLossTypes, setSelectedLossTypes)} /><i><Check size={11} /></i><span>{lossType}</span><small>{count}</small></label>)}</div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Código de arranque</b><button onClick={() => setSelectedStartCodes([])}>Limpiar</button></div>
            <div className="make-list">{startCodes.map(([startCode, count]) => <label key={startCode}><input type="checkbox" checked={selectedStartCodes.includes(startCode)} onChange={() => toggleValue(startCode, selectedStartCodes, setSelectedStartCodes)} /><i><Check size={11} /></i><span>{startCode}</span><small>{count}</small></label>)}</div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Llave</b><span>Reportada</span></div>
            <div className="source-pills" role="group" aria-label="Disponibilidad de llave"><button type="button" aria-pressed={keyMode === "all"} className={keyMode === "all" ? "source-pill source-pill--active" : "source-pill"} onClick={() => setKeyMode("all")}>Todos</button><button type="button" aria-pressed={keyMode === "with"} className={keyMode === "with" ? "source-pill source-pill--active" : "source-pill"} onClick={() => setKeyMode("with")}>Con llave</button><button type="button" aria-pressed={keyMode === "without"} className={keyMode === "without" ? "source-pill source-pill--active" : "source-pill"} onClick={() => setKeyMode("without")}>Sin llave</button></div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Tipo de tracción</b><button onClick={() => setSelectedDrives([])}>Limpiar</button></div>
            <div className="make-list">{drives.map(([drive, count]) => <label key={drive}><input type="checkbox" checked={selectedDrives.includes(drive)} onChange={() => toggleValue(drive, selectedDrives, setSelectedDrives)} /><i><Check size={11} /></i><span>{drive}</span><small>{count}</small></label>)}</div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Tipo de título</b><button onClick={() => setSelectedTitles(null)}>Predeterminado</button></div>
            <p className="filter-hint">Los títulos especiales están guardados, pero permanecen desmarcados y ocultos por defecto.</p>
            <div className="make-list">{titleTypes.map(([titleType, count]) => <label key={titleType}><input type="checkbox" checked={activeTitleTypes.includes(titleType)} onChange={() => toggleValue(titleType, activeTitleTypes, setSelectedTitles)} /><i><Check size={11} /></i><span>{titleType}</span><small>{count}</small></label>)}</div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Transmisión</b><button onClick={() => setSelectedTransmissions([])}>Limpiar</button></div>
            <div className="make-list">{transmissions.map(([transmission, count]) => <label key={transmission}><input type="checkbox" checked={selectedTransmissions.includes(transmission)} onChange={() => toggleValue(transmission, selectedTransmissions, setSelectedTransmissions)} /><i><Check size={11} /></i><span>{transmission}</span><small>{count}</small></label>)}</div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Tipo de combustible</b><button onClick={() => setSelectedFuels([])}>Limpiar</button></div>
            <div className="make-list">{fuels.map(([fuel, count]) => <label key={fuel}><input type="checkbox" checked={selectedFuels.includes(fuel)} onChange={() => toggleValue(fuel, selectedFuels, setSelectedFuels)} /><i><Check size={11} /></i><span>{fuel}</span><small>{count}</small></label>)}</div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Color exterior</b><button onClick={() => setSelectedColors([])}>Limpiar</button></div>
            <div className="make-list">{colors.map(([color, count]) => <label key={color}><input type="checkbox" checked={selectedColors.includes(color)} onChange={() => toggleValue(color, selectedColors, setSelectedColors)} /><i><Check size={11} /></i><span>{color}</span><small>{count}</small></label>)}</div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Motor</b><span>Litros</span></div>
            <div className="browse-range"><input value={minEngineSize} onChange={(event) => setMinEngineSize(event.target.value)} inputMode="decimal" aria-label="Motor litros mínimo" placeholder="0" /><span>—</span><input value={maxEngineSize} onChange={(event) => setMaxEngineSize(event.target.value)} inputMode="decimal" aria-label="Motor litros máximo" placeholder="10" /></div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Potencia</b><span>HP</span></div>
            <div className="browse-range"><input value={minHorsepower} onChange={(event) => setMinHorsepower(event.target.value)} inputMode="numeric" aria-label="Potencia mínima" placeholder="0" /><span>—</span><input value={maxHorsepower} onChange={(event) => setMaxHorsepower(event.target.value)} inputMode="numeric" aria-label="Potencia máxima" placeholder="1000" /></div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Tipo de motor</b><button onClick={() => setSelectedEngineLayouts([])}>Limpiar</button></div>
            <div className="make-list">{engineLayouts.map(([layout, count]) => <label key={layout}><input type="checkbox" checked={selectedEngineLayouts.includes(layout)} onChange={() => toggleValue(layout, selectedEngineLayouts, setSelectedEngineLayouts)} /><i><Check size={11} /></i><span>{layout}</span><small>{count}</small></label>)}</div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Cilindros</b><button onClick={() => setSelectedCylinders([])}>Limpiar</button></div>
            <div className="make-list">{cylinderOptions.map(([cylinders, count]) => <label key={cylinders}><input type="checkbox" checked={selectedCylinders.includes(cylinders)} onChange={() => toggleValue(cylinders, selectedCylinders, setSelectedCylinders)} /><i><Check size={11} /></i><span>{cylinders}</span><small>{count}</small></label>)}</div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Tipo de vendedor</b><button onClick={() => setSelectedSellerTypes([])}>Limpiar</button></div>
            <div className="make-list">{sellerTypes.map(([sellerType, count]) => <label key={sellerType}><input type="checkbox" checked={selectedSellerTypes.includes(sellerType)} onChange={() => toggleValue(sellerType, selectedSellerTypes, setSelectedSellerTypes)} /><i><Check size={11} /></i><span>{sellerType}</span><small>{count}</small></label>)}</div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Puja máxima</b><span>USD</span></div>
            <label className="browse-money"><span>$</span><input value={maxBid} onChange={(event) => setMaxBid(event.target.value)} inputMode="numeric" aria-label="Puja máxima" /></label>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Estado del lote</b><span>Del feed</span></div>
            <label className="browse-check"><input type="checkbox" checked={onlyBid} onChange={() => setOnlyBid(!onlyBid)} /><i><Check size={11} /></i><span>Solo con puja actual</span></label>
            <label className="browse-check"><input type="checkbox" checked={onlyPhotos} onChange={() => setOnlyPhotos(!onlyPhotos)} /><i><Check size={11} /></i><span>Solo con fotos reales</span></label>
          </section>
          <section className="filter-group filter-group--information"><div className="filter-group-label"><b>Campos del feed</b></div><p><CircleAlert size={14} /> Los campos no recibidos se muestran como “No reportado”.</p><p><CircleAlert size={14} /> El presupuesto LSC base no incluye fees de subasta, impuestos, transporte, asesoría ni reacondicionamiento/garantía.</p></section>
        </div>
        <div className="sidebar-footer"><button onClick={clearFilters}>Limpiar todos los filtros</button><span>{results.length} resultados</span></div>
      </aside>

      <section className="browse-results">
        <div className="browse-results-head"><div><p>INVENTARIO DISPONIBLE</p><h2>{results.length} vehículos</h2><span>{liveInventory.data?.generatedAt ? `Actualizado ${new Date(liveInventory.data.generatedAt).toLocaleString("es-US")}` : "Consultando corte Azure…"}</span></div><div className="browse-sort"><span>Ordenar</span><select value={sortMode} onChange={(event) => setSortMode(event.target.value as SortMode)} aria-label="Ordenar resultados"><option value="auction">Fecha: próxima primero</option><option value="auction-desc">Fecha: última primero</option><option value="estimate-low">Estimado: menor a mayor</option><option value="estimate-high">Estimado: mayor a menor</option><option value="bid-low">Puja: menor a mayor</option><option value="bid-high">Puja: mayor a menor</option><option value="buy-low">Buy Now: menor a mayor</option><option value="buy-high">Buy Now: mayor a menor</option><option value="year-low">Año: menor a mayor</option><option value="year-high">Año: mayor a menor</option><option value="odometer-low">Odómetro: menor a mayor</option><option value="odometer-high">Odómetro: mayor a menor</option></select></div></div>
        <div className="browse-status-tabs" role="tablist" aria-label="Estado de subasta">{AUCTION_STATUS_TABS.map(([value, label], index) => <button key={value} type="button" role="tab" aria-selected={auctionStatusMode === value} tabIndex={auctionStatusMode === value ? 0 : -1} className={auctionStatusMode === value ? "is-active" : ""} onClick={() => setAuctionStatusMode(value)} onKeyDown={(event) => handleAuctionStatusKeyDown(event, index)}>{label}</button>)}</div>
        <div className="browse-results-subhead"><span><Filter size={14} /> {selectedFacilities.length ? selectedFacilities.join(", ") : selectedStates.length ? selectedStates.join(", ") : selectedMakes.length ? selectedMakes.join(", ") : "Todos los vehículos"}</span><span>{auctionFrom || auctionTo ? "Fecha filtrada" : onlyBid ? "Con puja actual" : "Con y sin puja"}{minEstimatedTotal || maxEstimatedTotal ? " · Presupuesto LSC aplicado" : ""}{onlyPhotos ? " · Con fotos" : ""}</span></div>
        <div className="browse-list">
          {paginatedResults.map((vehicle) => <article className="browse-row" key={vehicle.lot}>
            <div className="browse-row-link">
              <VehiclePhotoCarousel photos={vehicle.photos} title={vehicle.title} lot={vehicle.lot} href={`/vehiculo/${vehicle.lot}`} />
              <a href={`/vehiculo/${vehicle.lot}`} target="_blank" rel="noreferrer" className="browse-row-details-link" aria-label={`Abrir ficha de ${vehicle.title} en una nueva pestaña`}>
              <div className="browse-vehicle-main"><div className="browse-lot"><b>{vehicle.title}</b><em>LOTE #{vehicle.lot}{vehicle.series ? ` · ${vehicle.series}` : ""}</em></div><div className="browse-specs"><span>{vehicle.hasKey === null ? "Llave N/R" : vehicle.hasKey ? "Con llave" : "Sin llave"}</span><span>{vehicle.transmission}</span><span>{vehicle.drive}</span><span>{vehicle.engineSize ? `${vehicle.engineSize}L` : "Motor N/R"}</span><span>{vehicle.cylinders !== "No reportado" ? `${vehicle.cylinders} cil.` : vehicle.cylinders}</span><span>{vehicle.horsepower ? `${vehicle.horsepower} HP` : "HP N/R"}</span></div><div className="browse-data browse-data--dense"><span><small>Millaje</small><b>{vehicle.odometer === null ? "No reportado" : `${Math.round(vehicle.odometer).toLocaleString()} mi`}</b></span><span><small>Vendedor</small><b>{vehicle.sellerType}</b></span><span><small>Documento</small><b>{vehicle.titleType}</b></span><span><small>Ubicación</small><b><MapPin size={13} /> {vehicle.location}</b></span><span><small>Daño</small><b>{vehicle.damage}</b></span><span><small>Estado</small><b>{vehicle.startCode}</b></span></div></div>
              <div className="browse-auction"><span className="auction-source">{vehicle.platform.toUpperCase()}{vehicle.has360 ? " · 360" : vehicle.hasVideo ? " · VIDEO" : ""}</span><div className="browse-provider-estimate"><small>ESTIMADO PROVEEDOR</small><b>{vehicle.providerEstimateMin !== null || vehicle.providerEstimateMax !== null ? `${formatMoney(vehicle.providerEstimateMin)} – ${formatMoney(vehicle.providerEstimateMax)}` : "No reportado"}</b></div><div><small><CalendarDays size={13} /> Subasta</small><b>{vehicle.auctionDate}</b></div><div className="browse-bid"><small>{vehicle.currentBid === null ? "PUJA" : "PUJA ACTUAL"}</small><b>{formatMoney(vehicle.currentBid)}</b></div>{vehicle.buyNow !== null && <div className="browse-buy-now"><small>BUY NOW</small><b>{formatMoney(vehicle.buyNow)}</b></div>}<div className="browse-estimate"><small>PRESUPUESTO LSC*</small><b>{vehicle.estimatedTotal ? `${formatMoney(vehicle.estimatedTotal.min)} – ${formatMoney(vehicle.estimatedTotal.max)}` : "Sin puja"}</b></div><strong>Ver ficha <ChevronUp size={15} /></strong></div>
              </a>
            </div>
          </article>)}
          {results.length === 0 && <div className="browse-empty"><Search size={30} /><h3>{liveInventory.isLoading ? "Cargando inventario…" : "No hay vehículos con esos filtros"}</h3><p>{liveInventory.isLoading ? "Consultando el corte persistido desde Azure." : "Prueba ampliando el año, la marca o el monto máximo."}</p>{!liveInventory.isLoading && <button onClick={clearFilters}>Restablecer búsqueda</button>}</div>}
        </div>
        {results.length > 0 && <nav className="inventory-pagination" aria-label="Paginación de vehículos">
          <span>Mostrando {(currentPage - 1) * INVENTORY_PAGE_SIZE + 1}–{Math.min(currentPage * INVENTORY_PAGE_SIZE, results.length)} de {results.length}</span>
          <div>
            <button disabled={currentPage === 1} onClick={() => setCurrentPage((page) => Math.max(1, page - 1))}>Anterior</button>
            {paginationPages.map((page, index) => <React.Fragment key={page}>
              {index > 0 && page - paginationPages[index - 1] > 1 && <span aria-hidden="true">…</span>}
              <button className={page === currentPage ? "is-active" : ""} aria-current={page === currentPage ? "page" : undefined} onClick={() => setCurrentPage(page)}>{page}</button>
            </React.Fragment>)}
            <button disabled={currentPage === totalPages} onClick={() => setCurrentPage((page) => Math.min(totalPages, page + 1))}>Siguiente</button>
          </div>
        </nav>}
      </section>
    </div>
    {filtersOpen && <button className="browse-backdrop" onClick={() => setFiltersOpen(false)} aria-label="Cerrar filtros" />}
  </main>;
}
