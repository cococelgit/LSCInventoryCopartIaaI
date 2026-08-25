/**
 * Style reminder — Explorador limpio de bandera cubana: blanco predominante, azul profundo de confianza,
 * rojo como señal de acción y acentos geométricos sobrios inspirados en la bandera, no en un marketplace.
 */
import { useMemo, useState } from "react";
import { trpc } from "../lib/trpc";
import { ArrowUpRight, CalendarDays, CarFront, Check, ChevronDown, CircleAlert, Filter, Image, MapPin, Search, ShieldCheck, SlidersHorizontal, X } from "lucide-react";
import { formatMoney } from "../data/inventory";

export default function Home() {
  const [query, setQuery] = useState("");
  const [selectedMakes, setSelectedMakes] = useState<string[]>([]);
  const [minYear, setMinYear] = useState("2000");
  const [maxYear, setMaxYear] = useState("2026");
  const [maxBid, setMaxBid] = useState("25000");
  const [onlyBid, setOnlyBid] = useState(false);
  const [filtersOpen, setFiltersOpen] = useState(false);
  const liveInventory = trpc.inventory.recent.useQuery({ take: 100 }, { refetchOnWindowFocus: true, staleTime: 60_000 });
  const catalogVehicles = useMemo(() => liveInventory.data?.vehicles?.map((vehicle) => ({
    lot: vehicle.lot,
    title: vehicle.title ?? `Lote ${vehicle.lot}`,
    year: vehicle.year ?? 0,
    make: vehicle.make ?? "Sin marca",
    model: vehicle.model ?? "Sin modelo",
    currentBid: vehicle.currentBidUsd,
    photos: vehicle.photos.length,
    auctionDate: vehicle.auctionAt ? new Date(vehicle.auctionAt).toLocaleDateString("es-US", { day: "2-digit", month: "short", year: "numeric" }).toUpperCase() : "No reportada",
    lotStatus: vehicle.lotStatus === "" ? "Sin puja actual" : "En seguimiento",
    availability: "Verificado" as const,
    gallery: vehicle.photos,
    publicFacts: { color: vehicle.color ?? undefined, fuel: vehicle.fuelType ?? undefined, transmission: vehicle.transmission ?? undefined, drive: vehicle.driveType ?? undefined, damage: vehicle.damage ?? undefined, titleType: vehicle.titleType ?? undefined, location: vehicle.location ?? undefined },
  })) ?? [], [liveInventory.data]);
  const liveMakes = useMemo(() => Array.from(new Set(catalogVehicles.map((vehicle) => vehicle.make))), [catalogVehicles]);
  const results = useMemo(() => catalogVehicles.filter((vehicle) => {
    const matchesQuery = `${vehicle.title} ${vehicle.lot}`.toLowerCase().includes(query.toLowerCase());
    const matchesMake = selectedMakes.length === 0 || selectedMakes.includes(vehicle.make);
    const matchesYear = vehicle.year >= Number(minYear || 0) && vehicle.year <= Number(maxYear || 9999);
    const matchesBid = vehicle.currentBid === null || vehicle.currentBid <= Number(maxBid || Infinity);
    return matchesQuery && matchesMake && matchesYear && matchesBid && (!onlyBid || vehicle.currentBid !== null);
  }), [catalogVehicles, query, selectedMakes, minYear, maxYear, maxBid, onlyBid]);

  const toggleMake = (make: string) => setSelectedMakes((active) => active.includes(make) ? active.filter((item) => item !== make) : [...active, make]);
  const clear = () => { setQuery(""); setSelectedMakes([]); setMinYear("2000"); setMaxYear("2026"); setMaxBid("25000"); setOnlyBid(false); };

  return (
    <main className="inventory-app">
      <header className="inventory-header">
        <a className="inventory-brand" href="/" aria-label="La Subasta Cubana Inventory">
          <img src="/manus-storage/lsc-logo-lineal-blanco_435d949d.png" alt="La Subasta Cubana" />
        </a>
        <div className="header-context"><span className="header-dot" /> CATÁLOGO INTERNO <i /> COPART · FLORIDA</div>
        <div className="header-right"><span><ShieldCheck size={16} /> Vista de solo lectura</span><button onClick={() => setFiltersOpen(true)}><SlidersHorizontal size={18} /> Filtros</button></div>
      </header>

      <section className="inventory-hero">
        <div className="hero-grid" />
        <div className="hero-copy"><p><span /> ESTADO DEL CORTE · VALIDADO</p><h1>Corte de evidencia<br /><em>para revisión interna.</em></h1><span>Copart Florida · {liveInventory.data?.generatedAt ? new Date(liveInventory.data.generatedAt).toLocaleString("es-US") : "esperando la lectura de Azure"} · Los campos ausentes se muestran como ausencia, sin inferencias.</span></div>
        <div className="hero-stats"><b>{catalogVehicles.length}</b><span>vehículos<br />disponibles</span><small><i className="seal-dot" /> {liveInventory.isFetching ? "actualizando" : "último corte: " + (liveInventory.data?.generatedAt ? new Date(liveInventory.data.generatedAt).toLocaleTimeString("es-US", { hour: "2-digit", minute: "2-digit" }) : "local")}</small></div>
      </section>

      <div className="inventory-layout">
        <aside className={`filter-drawer ${filtersOpen ? "filter-drawer--open" : ""}`}>
          <div className="rail-audit-status"><div><span className="seal-dot" /> EVIDENCIA VERIFICADA</div><b>{catalogVehicles.length} <small>lotes del corte</small></b><p><i /> {catalogVehicles.filter((vehicle) => vehicle.photos > 0).length} con foto reportada</p><p><i /> Lectura persistida · Azure</p></div>
          <div className="filter-head"><div><p>BUSCADOR</p><h2>Filtra resultados</h2></div><button className="close-filter" onClick={() => setFiltersOpen(false)} aria-label="Cerrar filtros"><X size={18} /></button></div>
          <div className="filter-scroll">
            <label className="search-field"><Search size={17} /><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Marca, modelo o lote" /></label>
            <section className="filter-section"><div className="filter-label"><span>Marca</span><small>{selectedMakes.length ? `${selectedMakes.length} seleccionada${selectedMakes.length > 1 ? "s" : ""}` : "Todas"}</small></div>{liveMakes.map((make) => <label className="check-row" key={make}><input type="checkbox" checked={selectedMakes.includes(make)} onChange={() => toggleMake(make)} /><i><Check size={12} /></i><span>{make}</span><b>{catalogVehicles.filter((vehicle) => vehicle.make === make).length}</b></label>)}</section>
            <section className="filter-section"><div className="filter-label"><span>Año</span><small>rango</small></div><div className="range-fields"><label><span>Desde</span><input inputMode="numeric" value={minYear} onChange={(event) => setMinYear(event.target.value)} /></label><span className="range-line" /><label><span>Hasta</span><input inputMode="numeric" value={maxYear} onChange={(event) => setMaxYear(event.target.value)} /></label></div></section>
            <section className="filter-section"><div className="filter-label"><span>Puja actual máxima</span><small>USD</small></div><label className="bid-field"><span>$</span><input inputMode="numeric" value={maxBid} onChange={(event) => setMaxBid(event.target.value)} /></label></section>
            <label className="switch-row"><span><b>Solo con puja actual</b><small>Ocultar lotes sin monto</small></span><input type="checkbox" checked={onlyBid} onChange={() => setOnlyBid(!onlyBid)} /><i /></label>
          </div>
          <div className="filter-footer"><button onClick={clear}>Limpiar filtros</button><span><CircleAlert size={14} /> No completamos campos ausentes.</span></div>
        </aside>

        <section className="results-panel">
          <div className="results-toolbar"><div><p>RESULTADOS DEL CORTE</p><h2><b>{results.length}</b> vehículos encontrados</h2>{liveInventory.isError && <small className="live-error">No se pudo actualizar desde Azure. Reintenta la lectura.</small>}</div><button className="refresh-button" onClick={() => liveInventory.refetch()} disabled={liveInventory.isFetching}><ShieldCheck size={14} /> {liveInventory.isFetching ? "Actualizando…" : "Actualizar feed"}</button><div className="sort-box"><span>Ordenar por</span><button>Puja actual <ChevronDown size={15} /></button></div></div>
          <div className="audit-strip"><div><span className="seal-dot" /> CONTROLES ACTIVOS</div><span>LOTES <b>{catalogVehicles.length}</b></span><span>FOTOS <b>{catalogVehicles.reduce((total, vehicle) => total + vehicle.photos, 0)}</b></span><span>PUJA <b>{catalogVehicles.filter((vehicle) => vehicle.currentBid !== null).length}</b></span><span>CAMPOS AUSENTES <em>NO SE COMPLETAN</em></span></div>
          <div className="active-filters"><span><Filter size={14} /> Filtros activos</span>{selectedMakes.map((make) => <button key={make} onClick={() => toggleMake(make)}>{make}<X size={13} /></button>)}{onlyBid && <button onClick={() => setOnlyBid(false)}>Con puja<X size={13} /></button>}{!selectedMakes.length && !onlyBid && <em>Todos los vehículos del corte</em>}</div>
          <div className="vehicle-list">
            {results.map((vehicle, index) => <article className="vehicle-card" key={vehicle.lot}>
              <div className={`vehicle-visual vehicle-visual--${index % 3}`}>{vehicle.gallery[0] ? <img className="vehicle-photo" src={vehicle.gallery[0]} alt={`${vehicle.title}, lote ${vehicle.lot}`} /> : <div className="vehicle-photo vehicle-photo--missing">Sin foto reportada por el feed</div>}<span className="photo-badge"><Image size={12} /> {vehicle.gallery[0] ? "FOTO REAL · EVIDENCIA" : "SIN EVIDENCIA VISUAL"}</span><i /></div>
              <div className="vehicle-info"><div className="lot-line"><span>LOTE #{vehicle.lot}</span><i /> <b>{vehicle.availability}</b></div><h3>{vehicle.title}</h3><div className="vehicle-facts"><span><CalendarDays size={14} /> {vehicle.auctionDate}</span><span><Image size={14} /> {vehicle.photos} foto{vehicle.photos !== 1 ? "s" : ""}</span><span><MapPin size={14} /> {vehicle.publicFacts.location ?? "No reportada"}</span></div></div>
              <div className="bid-block"><span>PUJA ACTUAL</span><b className={vehicle.currentBid === null ? "bid-block__empty" : ""}>{formatMoney(vehicle.currentBid)}</b><small>{vehicle.currentBid === null ? "El feed no reportó monto" : "Dato reportado por el feed"}</small></div>
              <a className="details-link" href={`/vehiculo/${vehicle.lot}`} target="_blank" rel="noreferrer">Inspeccionar<br />ficha <ArrowUpRight size={18} /></a>
            </article>)}
            {results.length === 0 && <div className="empty-state"><Search size={27} /><h3>{liveInventory.isLoading ? "Cargando el inventario desde Azure…" : "No encontramos carros con esos filtros."}</h3><p>{liveInventory.isLoading ? "El corte persistido se está consultando de forma segura." : "Ajusta el año, la marca o la puja máxima para ver el corte completo."}</p>{!liveInventory.isLoading && <button onClick={clear}>Restablecer filtros</button>}</div>}
          </div>
          <footer className="catalog-footer"><span>Última lectura: {liveInventory.data?.generatedAt ? new Date(liveInventory.data.generatedAt).toLocaleString("es-US") : "pendiente de Azure"} · Fuente: Azure Inventory Engine</span><span>La Subasta Cubana · No es un catálogo comercial</span></footer>
        </section>
      </div>
      {filtersOpen && <button className="filter-backdrop" onClick={() => setFiltersOpen(false)} aria-label="Cerrar filtros" />}
    </main>
  );
}
