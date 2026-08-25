/** Inventario operativo: búsqueda y comparación rápidas, sin elementos decorativos que resten espacio a los lotes. */
import { useMemo, useState } from "react";
import { trpc } from "../lib/trpc";
import { CalendarDays, Check, ChevronDown, ChevronUp, CircleAlert, Filter, Image, MapPin, Search, SlidersHorizontal, Tag, X } from "lucide-react";
import { formatMoney } from "../data/inventory";
import { LSC_BROKER_FEE_MAX_USD, LSC_BROKER_FEE_MIN_USD } from "../data/lscPricing";
import "./home-filters.css";

type SortMode = "auction" | "bid-low" | "bid-high";

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
  return facilityId ? `${location} · Facility ${facilityId}` : null;
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

export default function Home() {
  const [query, setQuery] = useState("");
  const [selectedMakes, setSelectedMakes] = useState<string[]>([]);
  const [minYear, setMinYear] = useState("2000");
  const [maxYear, setMaxYear] = useState("2026");
  const [maxBid, setMaxBid] = useState("25000");
  const [onlyBid, setOnlyBid] = useState(false);
  const [onlyPhotos, setOnlyPhotos] = useState(false);
  const [selectedDamages, setSelectedDamages] = useState<string[]>([]);
  const [selectedTitles, setSelectedTitles] = useState<string[]>([]);
  const [selectedTransmissions, setSelectedTransmissions] = useState<string[]>([]);
  const [selectedModels, setSelectedModels] = useState<string[]>([]);
  const [selectedVehicleTypes, setSelectedVehicleTypes] = useState<string[]>([]);
  const [selectedStartCodes, setSelectedStartCodes] = useState<string[]>([]);
  const [selectedDrives, setSelectedDrives] = useState<string[]>([]);
  const [selectedFuels, setSelectedFuels] = useState<string[]>([]);
  const [selectedFacilities, setSelectedFacilities] = useState<string[]>([]);
  const [selectedStates, setSelectedStates] = useState<string[]>([]);
  const [auctionFrom, setAuctionFrom] = useState("");
  const [auctionTo, setAuctionTo] = useState("");
  const [minEstimatedTotal, setMinEstimatedTotal] = useState("");
  const [maxEstimatedTotal, setMaxEstimatedTotal] = useState("");
  const [minOdometer, setMinOdometer] = useState("");
  const [maxOdometer, setMaxOdometer] = useState("");
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [sortMode, setSortMode] = useState<SortMode>("auction");
  const liveInventory = trpc.inventory.recent.useQuery({ take: 100 }, { refetchOnWindowFocus: true, staleTime: 60_000 });

  const vehicles = useMemo(() => liveInventory.data?.vehicles?.map((vehicle) => {
    const location = vehicle.location ?? "No reportada";
    const facilityId = vehicle.facilityId ?? null;
    return {
      lot: vehicle.lot,
      title: vehicle.title ?? `Lote ${vehicle.lot}`,
      year: vehicle.year ?? 0,
      make: vehicle.make ?? "Sin marca",
      model: vehicle.model ?? "Sin modelo",
      currentBid: vehicle.currentBidUsd,
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
      transmission: vehicle.transmission ?? "No reportada",
      fuel: vehicle.fuelType ?? "No reportado",
      drive: vehicle.driveType ?? "No reportada",
      titleType: vehicle.titleType ?? "No reportado",
      vehicleType: vehicle.vehicleType ?? "No reportado",
      startCode: "No reportado",
      odometer: parseOdometer(vehicle.odometer),
      status: vehicle.lotStatus || "En seguimiento",
    };
  }) ?? [], [liveInventory.data]);

  const makes = useMemo(() => Array.from(new Set(vehicles.map((vehicle) => vehicle.make))).sort(), [vehicles]);
  const models = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.model)), [vehicles]);
  const vehicleTypes = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.vehicleType)), [vehicles]);
  const damages = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.damage)), [vehicles]);
  const titleTypes = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.titleType)), [vehicles]);
  const transmissions = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.transmission)), [vehicles]);
  const startCodes = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.startCode)), [vehicles]);
  const drives = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.drive)), [vehicles]);
  const fuels = useMemo(() => buildOptionCounts(vehicles.map((vehicle) => vehicle.fuel)), [vehicles]);
  const facilities = useMemo(() => buildOptionCounts(vehicles.flatMap((vehicle) => vehicle.facilityLabel ? [vehicle.facilityLabel] : [])), [vehicles]);
  const states = useMemo(() => buildOptionCounts(vehicles.flatMap((vehicle) => vehicle.state !== "No reportado" ? [vehicle.state] : [])), [vehicles]);
  const filteredVehicles = useMemo(() => vehicles.filter((vehicle) => {
    const normalized = `${vehicle.title} ${vehicle.lot} ${vehicle.make} ${vehicle.model}`.toLowerCase();
    const appliesOdometer = Boolean(minOdometer || maxOdometer);
    const appliesAuctionDate = Boolean(auctionFrom || auctionTo);
    const appliesEstimatedTotal = Boolean(minEstimatedTotal || maxEstimatedTotal);
    return normalized.includes(query.toLowerCase())
      && (selectedMakes.length === 0 || selectedMakes.includes(vehicle.make))
      && (selectedModels.length === 0 || selectedModels.includes(vehicle.model))
      && (selectedFacilities.length === 0 || (vehicle.facilityLabel !== null && selectedFacilities.includes(vehicle.facilityLabel)))
      && (selectedStates.length === 0 || selectedStates.includes(vehicle.state))
      && vehicle.year >= Number(minYear || 0)
      && vehicle.year <= Number(maxYear || 9999)
      && (!appliesAuctionDate || isAuctionDateInRange(vehicle.auctionDateKey, auctionFrom, auctionTo))
      && (!appliesOdometer || (vehicle.odometer !== null && vehicle.odometer >= Number(minOdometer || 0) && vehicle.odometer <= Number(maxOdometer || Number.MAX_SAFE_INTEGER)))
      && (!appliesEstimatedTotal || doesEstimatedTotalOverlap(vehicle.estimatedTotal, minEstimatedTotal, maxEstimatedTotal))
      && (vehicle.currentBid === null || vehicle.currentBid <= Number(maxBid || Infinity))
      && (!onlyBid || vehicle.currentBid !== null)
      && (!onlyPhotos || vehicle.photos.length > 0)
      && (selectedVehicleTypes.length === 0 || selectedVehicleTypes.includes(vehicle.vehicleType))
      && (selectedDamages.length === 0 || selectedDamages.includes(vehicle.damage))
      && (selectedTitles.length === 0 || selectedTitles.includes(vehicle.titleType))
      && (selectedStartCodes.length === 0 || selectedStartCodes.includes(vehicle.startCode))
      && (selectedDrives.length === 0 || selectedDrives.includes(vehicle.drive))
      && (selectedTransmissions.length === 0 || selectedTransmissions.includes(vehicle.transmission))
      && (selectedFuels.length === 0 || selectedFuels.includes(vehicle.fuel));
  }), [vehicles, query, selectedMakes, selectedModels, selectedFacilities, selectedStates, minYear, maxYear, auctionFrom, auctionTo, minOdometer, maxOdometer, minEstimatedTotal, maxEstimatedTotal, maxBid, onlyBid, onlyPhotos, selectedVehicleTypes, selectedDamages, selectedTitles, selectedStartCodes, selectedDrives, selectedTransmissions, selectedFuels]);
  const results = useMemo(() => [...filteredVehicles].sort((left, right) => {
    if (sortMode === "bid-low") return (left.currentBid ?? Number.MAX_SAFE_INTEGER) - (right.currentBid ?? Number.MAX_SAFE_INTEGER);
    if (sortMode === "bid-high") return (right.currentBid ?? -1) - (left.currentBid ?? -1);
    return (new Date(left.auctionAt ?? "2999-12-31").getTime()) - (new Date(right.auctionAt ?? "2999-12-31").getTime());
  }), [filteredVehicles, sortMode]);

  const toggleMake = (make: string) => setSelectedMakes((active) => active.includes(make) ? active.filter((item) => item !== make) : [...active, make]);
  const toggleValue = (value: string, active: string[], update: (next: string[]) => void) => update(active.includes(value) ? active.filter((item) => item !== value) : [...active, value]);
  const clearFilters = () => { setQuery(""); setSelectedMakes([]); setSelectedModels([]); setSelectedVehicleTypes([]); setSelectedDamages([]); setSelectedTitles([]); setSelectedStartCodes([]); setSelectedDrives([]); setSelectedTransmissions([]); setSelectedFuels([]); setSelectedFacilities([]); setSelectedStates([]); setMinYear("2000"); setMaxYear("2026"); setAuctionFrom(""); setAuctionTo(""); setMinOdometer(""); setMaxOdometer(""); setMinEstimatedTotal(""); setMaxEstimatedTotal(""); setMaxBid("25000"); setOnlyBid(false); setOnlyPhotos(false); };
  const sortLabel = sortMode === "auction" ? "Fecha de subasta" : sortMode === "bid-low" ? "Puja: menor a mayor" : "Puja: mayor a menor";

  return <main className="browse-page">
    <header className="browse-header">
      <a className="browse-brand" href="/" aria-label="La Subasta Cubana"><img src="/manus-storage/lsc-logo-lineal-blanco_435d949d.png" alt="La Subasta Cubana" /></a>
      <div className="browse-search" role="search"><span className="browse-scope">Inventario <ChevronDown size={14} /></span><Search size={19} /><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Busca por marca, modelo o número de lote" aria-label="Buscar vehículos" /></div>
      <div className="browse-header-meta"><span><i /> COPART · FLORIDA</span><small>{vehicles.length} lotes</small></div>
      <button className="browse-filter-button" onClick={() => setFiltersOpen(true)}><SlidersHorizontal size={18} /> Filtros</button>
    </header>

    <div className="browse-shell">
      <aside className={`browse-sidebar ${filtersOpen ? "browse-sidebar--open" : ""}`}>
        <div className="sidebar-top"><div><p>FILTROS</p><h1>Busca tu carro</h1></div><button onClick={() => setFiltersOpen(false)} aria-label="Cerrar filtros"><X size={18} /></button></div>
        <div className="sidebar-scroll">
          <section className="filter-group">
            <div className="filter-group-label"><b>Fuente</b><span>Disponible</span></div>
            <div className="source-pills"><button className="source-pill source-pill--active">Copart</button><button className="source-pill" disabled>IAAI · pronto</button></div>
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
            <div className="browse-range"><input value={minEstimatedTotal} onChange={(event) => setMinEstimatedTotal(event.target.value)} inputMode="numeric" aria-label="Costo estimado mínimo" placeholder="Desde" /><span>—</span><input value={maxEstimatedTotal} onChange={(event) => setMaxEstimatedTotal(event.target.value)} inputMode="numeric" aria-label="Costo estimado máximo" placeholder="Hasta" /></div>
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
            <div className="filter-group-label"><b>Tipo de daño</b><button onClick={() => setSelectedDamages([])}>Limpiar</button></div>
            <div className="make-list">{damages.map(([damage, count]) => <label key={damage}><input type="checkbox" checked={selectedDamages.includes(damage)} onChange={() => toggleValue(damage, selectedDamages, setSelectedDamages)} /><i><Check size={11} /></i><span>{damage}</span><small>{count}</small></label>)}</div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Código de arranque</b><button onClick={() => setSelectedStartCodes([])}>Limpiar</button></div>
            <div className="make-list">{startCodes.map(([startCode, count]) => <label key={startCode}><input type="checkbox" checked={selectedStartCodes.includes(startCode)} onChange={() => toggleValue(startCode, selectedStartCodes, setSelectedStartCodes)} /><i><Check size={11} /></i><span>{startCode}</span><small>{count}</small></label>)}</div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Tipo de tracción</b><button onClick={() => setSelectedDrives([])}>Limpiar</button></div>
            <div className="make-list">{drives.map(([drive, count]) => <label key={drive}><input type="checkbox" checked={selectedDrives.includes(drive)} onChange={() => toggleValue(drive, selectedDrives, setSelectedDrives)} /><i><Check size={11} /></i><span>{drive}</span><small>{count}</small></label>)}</div>
          </section>
          <section className="filter-group">
            <div className="filter-group-label"><b>Estado del título</b><button onClick={() => setSelectedTitles([])}>Limpiar</button></div>
            <div className="make-list">{titleTypes.map(([titleType, count]) => <label key={titleType}><input type="checkbox" checked={selectedTitles.includes(titleType)} onChange={() => toggleValue(titleType, selectedTitles, setSelectedTitles)} /><i><Check size={11} /></i><span>{titleType}</span><small>{count}</small></label>)}</div>
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
            <div className="filter-group-label"><b>Puja máxima</b><span>USD</span></div>
            <label className="browse-money"><span>$</span><input value={maxBid} onChange={(event) => setMaxBid(event.target.value)} inputMode="numeric" /></label>
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
        <div className="browse-results-head"><div><p>INVENTARIO DISPONIBLE</p><h2>{results.length} vehículos</h2><span>{liveInventory.data?.generatedAt ? `Actualizado ${new Date(liveInventory.data.generatedAt).toLocaleString("es-US")}` : "Consultando corte Azure…"}</span></div><div className="browse-sort"><span>Ordenar</span><select value={sortMode} onChange={(event) => setSortMode(event.target.value as SortMode)} aria-label="Ordenar resultados"><option value="auction">Fecha de subasta</option><option value="bid-low">Puja: menor a mayor</option><option value="bid-high">Puja: mayor a menor</option></select></div></div>
        <div className="browse-results-subhead"><span><Filter size={14} /> {selectedFacilities.length ? selectedFacilities.join(", ") : selectedStates.length ? selectedStates.join(", ") : selectedMakes.length ? selectedMakes.join(", ") : "Todos los vehículos"}</span><span>{auctionFrom || auctionTo ? "Fecha filtrada" : onlyBid ? "Con puja actual" : "Con y sin puja"}{minEstimatedTotal || maxEstimatedTotal ? " · Presupuesto LSC aplicado" : ""}{onlyPhotos ? " · Con fotos" : ""}</span></div>
        <div className="browse-list">
          {results.map((vehicle) => <article className="browse-row" key={vehicle.lot}>
            <a href={`/vehiculo/${vehicle.lot}`} target="_blank" rel="noreferrer" className="browse-row-link" aria-label={`Abrir ficha de ${vehicle.title} en una nueva pestaña`}>
              <div className="browse-photo">{vehicle.photos[0] ? <img src={vehicle.photos[0]} alt={`${vehicle.title}, lote ${vehicle.lot}`} /> : <div className="browse-photo-empty"><Image size={28} /><span>Sin foto</span></div>}<span><Image size={12} /> {vehicle.photos.length} fotos</span></div>
              <div className="browse-vehicle-main"><div className="browse-lot"><b>{vehicle.title}</b><em>LOTE #{vehicle.lot}</em></div><div className="browse-specs"><span>{vehicle.year || "Año N/R"}</span><span>{vehicle.transmission}</span><span>{vehicle.fuel}</span><span>{vehicle.drive}</span></div><div className="browse-data"><span><small>Ubicación</small><b><MapPin size={13} /> {vehicle.location}</b></span><span><small>Daño</small><b>{vehicle.damage}</b></span><span><small>Título</small><b>{vehicle.titleType}</b></span></div></div>
              <div className="browse-auction"><span className="auction-source">COPART</span><div><small><CalendarDays size={13} /> Subasta</small><b>{vehicle.auctionDate}</b></div><div className="browse-bid"><small>PUJA ACTUAL</small><b>{formatMoney(vehicle.currentBid)}</b></div><div className="browse-estimate"><small>PRESUPUESTO LSC*</small><b>{vehicle.estimatedTotal ? `${formatMoney(vehicle.estimatedTotal.min)} – ${formatMoney(vehicle.estimatedTotal.max)}` : "Sin puja"}</b></div><strong>Ver ficha <ChevronUp size={15} /></strong></div>
            </a>
          </article>)}
          {results.length === 0 && <div className="browse-empty"><Search size={30} /><h3>{liveInventory.isLoading ? "Cargando inventario…" : "No hay vehículos con esos filtros"}</h3><p>{liveInventory.isLoading ? "Consultando el corte persistido desde Azure." : "Prueba ampliando el año, la marca o el monto máximo."}</p>{!liveInventory.isLoading && <button onClick={clearFilters}>Restablecer búsqueda</button>}</div>}
        </div>
      </section>
    </div>
    {filtersOpen && <button className="browse-backdrop" onClick={() => setFiltersOpen(false)} aria-label="Cerrar filtros" />}
  </main>;
}
