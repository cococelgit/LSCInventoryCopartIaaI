/** Inventario operativo: búsqueda y comparación rápidas, sin elementos decorativos que resten espacio a los lotes. */
import { useMemo, useState } from "react";
import { trpc } from "../lib/trpc";
import { CalendarDays, Check, ChevronDown, ChevronUp, CircleAlert, Filter, Image, MapPin, Search, SlidersHorizontal, Tag, X } from "lucide-react";
import { formatMoney } from "../data/inventory";

type SortMode = "auction" | "bid-low" | "bid-high";

export default function Home() {
  const [query, setQuery] = useState("");
  const [selectedMakes, setSelectedMakes] = useState<string[]>([]);
  const [minYear, setMinYear] = useState("2000");
  const [maxYear, setMaxYear] = useState("2026");
  const [maxBid, setMaxBid] = useState("25000");
  const [onlyBid, setOnlyBid] = useState(false);
  const [onlyPhotos, setOnlyPhotos] = useState(false);
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [sortMode, setSortMode] = useState<SortMode>("auction");
  const liveInventory = trpc.inventory.recent.useQuery({ take: 100 }, { refetchOnWindowFocus: true, staleTime: 60_000 });

  const vehicles = useMemo(() => liveInventory.data?.vehicles?.map((vehicle) => ({
    lot: vehicle.lot,
    title: vehicle.title ?? `Lote ${vehicle.lot}`,
    year: vehicle.year ?? 0,
    make: vehicle.make ?? "Sin marca",
    model: vehicle.model ?? "Sin modelo",
    currentBid: vehicle.currentBidUsd,
    photos: vehicle.photos,
    auctionAt: vehicle.auctionAt,
    auctionDate: vehicle.auctionAt ? new Date(vehicle.auctionAt).toLocaleDateString("es-US", { day: "2-digit", month: "short", year: "numeric" }).toUpperCase() : "No reportada",
    location: vehicle.location ?? "No reportada",
    damage: vehicle.damage ?? "No reportado",
    transmission: vehicle.transmission ?? "No reportada",
    fuel: vehicle.fuelType ?? "No reportado",
    drive: vehicle.driveType ?? "No reportada",
    titleType: vehicle.titleType ?? "No reportado",
    status: vehicle.lotStatus || "En seguimiento",
  })) ?? [], [liveInventory.data]);

  const makes = useMemo(() => Array.from(new Set(vehicles.map((vehicle) => vehicle.make))).sort(), [vehicles]);
  const filteredVehicles = useMemo(() => vehicles.filter((vehicle) => {
    const normalized = `${vehicle.title} ${vehicle.lot} ${vehicle.make} ${vehicle.model}`.toLowerCase();
    return normalized.includes(query.toLowerCase())
      && (selectedMakes.length === 0 || selectedMakes.includes(vehicle.make))
      && vehicle.year >= Number(minYear || 0)
      && vehicle.year <= Number(maxYear || 9999)
      && (vehicle.currentBid === null || vehicle.currentBid <= Number(maxBid || Infinity))
      && (!onlyBid || vehicle.currentBid !== null)
      && (!onlyPhotos || vehicle.photos.length > 0);
  }), [vehicles, query, selectedMakes, minYear, maxYear, maxBid, onlyBid, onlyPhotos]);
  const results = useMemo(() => [...filteredVehicles].sort((left, right) => {
    if (sortMode === "bid-low") return (left.currentBid ?? Number.MAX_SAFE_INTEGER) - (right.currentBid ?? Number.MAX_SAFE_INTEGER);
    if (sortMode === "bid-high") return (right.currentBid ?? -1) - (left.currentBid ?? -1);
    return (new Date(left.auctionAt ?? "2999-12-31").getTime()) - (new Date(right.auctionAt ?? "2999-12-31").getTime());
  }), [filteredVehicles, sortMode]);

  const toggleMake = (make: string) => setSelectedMakes((active) => active.includes(make) ? active.filter((item) => item !== make) : [...active, make]);
  const clearFilters = () => { setQuery(""); setSelectedMakes([]); setMinYear("2000"); setMaxYear("2026"); setMaxBid("25000"); setOnlyBid(false); setOnlyPhotos(false); };
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
            <div className="filter-group-label"><b>Año</b><span>Rango</span></div>
            <div className="browse-range"><input value={minYear} onChange={(event) => setMinYear(event.target.value)} inputMode="numeric" aria-label="Año mínimo" /><span>—</span><input value={maxYear} onChange={(event) => setMaxYear(event.target.value)} inputMode="numeric" aria-label="Año máximo" /></div>
          </section>
          <section className="filter-group filter-group--makes">
            <div className="filter-group-label"><b>Marca</b><button onClick={() => setSelectedMakes([])}>Limpiar</button></div>
            <div className="make-list">{makes.map((make) => <label key={make}><input type="checkbox" checked={selectedMakes.includes(make)} onChange={() => toggleMake(make)} /><i><Check size={11} /></i><span>{make}</span><small>{vehicles.filter((vehicle) => vehicle.make === make).length}</small></label>)}</div>
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
          <section className="filter-group filter-group--information"><div className="filter-group-label"><b>Campos del feed</b></div><p><CircleAlert size={14} /> Los campos no recibidos se muestran como “No reportado”.</p></section>
        </div>
        <div className="sidebar-footer"><button onClick={clearFilters}>Limpiar todos los filtros</button><span>{results.length} resultados</span></div>
      </aside>

      <section className="browse-results">
        <div className="browse-results-head"><div><p>INVENTARIO DISPONIBLE</p><h2>{results.length} vehículos</h2><span>{liveInventory.data?.generatedAt ? `Actualizado ${new Date(liveInventory.data.generatedAt).toLocaleString("es-US")}` : "Consultando corte Azure…"}</span></div><div className="browse-sort"><span>Ordenar</span><select value={sortMode} onChange={(event) => setSortMode(event.target.value as SortMode)} aria-label="Ordenar resultados"><option value="auction">Fecha de subasta</option><option value="bid-low">Puja: menor a mayor</option><option value="bid-high">Puja: mayor a menor</option></select></div></div>
        <div className="browse-results-subhead"><span><Filter size={14} /> {selectedMakes.length ? selectedMakes.join(", ") : "Todos los vehículos"}</span><span>{onlyBid ? "Con puja actual" : "Con y sin puja"}{onlyPhotos ? " · Con fotos" : ""}</span></div>
        <div className="browse-list">
          {results.map((vehicle) => <article className="browse-row" key={vehicle.lot}>
            <a href={`/vehiculo/${vehicle.lot}`} target="_blank" rel="noreferrer" className="browse-row-link" aria-label={`Abrir ficha de ${vehicle.title} en una nueva pestaña`}>
              <div className="browse-photo">{vehicle.photos[0] ? <img src={vehicle.photos[0]} alt={`${vehicle.title}, lote ${vehicle.lot}`} /> : <div className="browse-photo-empty"><Image size={28} /><span>Sin foto</span></div>}<span><Image size={12} /> {vehicle.photos.length} fotos</span></div>
              <div className="browse-vehicle-main"><div className="browse-lot"><b>{vehicle.title}</b><em>LOTE #{vehicle.lot}</em></div><div className="browse-specs"><span>{vehicle.year || "Año N/R"}</span><span>{vehicle.transmission}</span><span>{vehicle.fuel}</span><span>{vehicle.drive}</span></div><div className="browse-data"><span><small>Ubicación</small><b><MapPin size={13} /> {vehicle.location}</b></span><span><small>Daño</small><b>{vehicle.damage}</b></span><span><small>Título</small><b>{vehicle.titleType}</b></span></div></div>
              <div className="browse-auction"><span className="auction-source">COPART</span><div><small><CalendarDays size={13} /> Subasta</small><b>{vehicle.auctionDate}</b></div><div className="browse-bid"><small>PUJA ACTUAL</small><b>{formatMoney(vehicle.currentBid)}</b></div><strong>Ver ficha <ChevronUp size={15} /></strong></div>
            </a>
          </article>)}
          {results.length === 0 && <div className="browse-empty"><Search size={30} /><h3>{liveInventory.isLoading ? "Cargando inventario…" : "No hay vehículos con esos filtros"}</h3><p>{liveInventory.isLoading ? "Consultando el corte persistido desde Azure." : "Prueba ampliando el año, la marca o el monto máximo."}</p>{!liveInventory.isLoading && <button onClick={clearFilters}>Restablecer búsqueda</button>}</div>}
        </div>
      </section>
    </div>
    {filtersOpen && <button className="browse-backdrop" onClick={() => setFiltersOpen(false)} aria-label="Cerrar filtros" />}
  </main>;
}
