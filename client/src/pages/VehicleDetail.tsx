import React, { useState } from "react";
import { ArrowLeft, Building2, CalendarDays, CarFront, CircleAlert, FileCheck2, Fuel, Gauge, Gavel, Image, KeyRound, MapPin, Palette, Wrench } from "lucide-react";
import { trpc } from "../lib/trpc";
import { formatMoney } from "../data/inventory";
import "./vehicle-detail.css";

function value(value: string | number | boolean | null | undefined, fallback = "No reportado") {
  if (value === null || value === undefined || value === "") return fallback;
  if (typeof value === "boolean") return value ? "Sí" : "No";
  return String(value);
}

function money(value: number | null | undefined) {
  return formatMoney(value ?? null);
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return <div className="vehicle-field"><small>{label}</small><b>{children}</b></div>;
}

export default function VehicleDetail({ params }: { params: { lot: string } }) {
  const liveVehicle = trpc.inventory.vehicle.useQuery({ lot: params.lot }, { retry: false, staleTime: 60_000 });
  const [activePhoto, setActivePhoto] = useState(0);
  if (liveVehicle.isLoading) return <main className="vehicle-detail-page"><section className="vehicle-detail-loading"><CarFront size={42} /><h1>Cargando ficha desde Azure…</h1></section></main>;
  const vehicle = liveVehicle.data;
  if (!vehicle) return <main className="vehicle-detail-page"><section className="vehicle-detail-loading"><CarFront size={42} /><h1>Lote no disponible.</h1><a href="/">Volver al inventario</a></section></main>;
  const gallery = vehicle.photos ?? [];
  const title = vehicle.title ?? (`${vehicle.year ?? ""} ${vehicle.make ?? ""} ${vehicle.model ?? ""}`.trim() || `Lote ${vehicle.lot}`);
  const auctionDate = vehicle.auctionAt ? new Date(vehicle.auctionAt).toLocaleString("es-US", { timeZone: "America/New_York", day: "2-digit", month: "short", year: "numeric", hour: "numeric", minute: "2-digit" }) : "No reportada";
  return <main className="vehicle-detail-page">
    <header className="vehicle-detail-header"><a href="/"><img src="/manus-storage/lsc-logo-lineal-blanco_435d949d.png" alt="La Subasta Cubana" /></a><span>FICHA DE SUBASTA · DATOS PERSISTIDOS</span></header>
    <div className="vehicle-detail-shell">
      <a href="/" className="vehicle-detail-back"><ArrowLeft size={15} /> Volver al inventario</a>
      <div className="vehicle-detail-titlebar"><div><p>LOTE #{vehicle.lot}</p><h1>{title}</h1></div><div><span className="vehicle-detail-chip vehicle-detail-chip--source">{vehicle.platform.toUpperCase()}</span><span className="vehicle-detail-chip"><MapPin size={13} /> {value(vehicle.location)}</span><span className="vehicle-detail-chip"><CalendarDays size={13} /> {auctionDate}</span></div></div>
      <section className="vehicle-detail-top">
        <div className="vehicle-gallery-card" role="region" aria-label={`Galería de ${title}`}><div className="vehicle-gallery-main">{gallery.length ? <img src={gallery[Math.min(activePhoto, gallery.length - 1)]} alt={`${title}, foto ${activePhoto + 1}`} /> : <div className="vehicle-gallery-empty">El feed no reportó fotos.</div>}<span aria-live="polite"><Image size={13} /> {gallery.length ? `${activePhoto + 1} / ${gallery.length}` : "Sin fotos"}{vehicle.has360 ? " · 360°" : ""}{vehicle.hasVideo ? " · Video" : ""}</span></div>{gallery.length > 0 && <div className="vehicle-thumbs" role="group" aria-label="Galería de fotos">{gallery.map((photo, index) => <button type="button" key={`${photo}-${index}`} className={index === activePhoto ? "vehicle-thumb is-active" : "vehicle-thumb"} onClick={() => setActivePhoto(index)} aria-label={`Ver foto ${index + 1}`} aria-pressed={index === activePhoto}><img src={photo} alt="" loading="lazy" /></button>)}</div>}</div>
        <aside className="vehicle-auction-card"><h2>Estado de subasta</h2><div className="vehicle-auction-price"><small>PUJA ACTUAL</small><b>{money(vehicle.currentBidUsd)}</b></div><div className="vehicle-price-grid"><div><small>PRE-BID</small><b>{money(vehicle.preBidUsd)}</b></div><div className="is-buy"><small>BUY NOW</small><b>{money(vehicle.buyNowUsd)}</b></div><div><small>ESTIMADO DESDE</small><b>{money(vehicle.estimatedPriceFromUsd)}</b></div><div><small>ESTIMADO HASTA</small><b>{money(vehicle.estimatedPriceToUsd)}</b></div></div><div className="vehicle-auction-meta"><span><Gavel size={14} /> Estado <b>{value(vehicle.lotStatus)}</b></span><span><CalendarDays size={14} /> Fecha <b>{auctionDate}</b></span><span><Building2 size={14} /> Branch <b>{value(vehicle.sellingBranch ?? vehicle.location)}</b></span><span><MapPin size={14} /> Lane / Aisle <b>{value([vehicle.lane, vehicle.aisle].filter(Boolean).join(" / "))}</b></span></div><p className="vehicle-auction-note">Los precios y estados son reportados por el proveedor. No constituyen una cotización final de La Subasta Cubana.</p></aside>
      </section>
      <div className="vehicle-sections">
        <section className="vehicle-section"><h2><CarFront size={18} /> Información del vehículo</h2><div className="vehicle-field-grid"><Field label="Año">{value(vehicle.year)}</Field><Field label="Marca / Modelo">{value([vehicle.make, vehicle.model].filter(Boolean).join(" "))}</Field><Field label="Serie">{value(vehicle.series)}</Field><Field label="Tipo">{value(vehicle.vehicleType)}</Field><Field label="Body style">{value(vehicle.bodyStyle)}</Field><Field label="Color">{value(vehicle.color)}</Field><Field label="Odómetro">{vehicle.odometer === null ? "No reportado" : `${Math.round(vehicle.odometer).toLocaleString()} mi`}</Field><Field label="Estado odómetro">{value(vehicle.odometerStatus)}</Field><Field label="VIN status">{value(vehicle.vinStatus)}</Field><Field label="Fabricado en">{value(vehicle.manufacturedIn)}</Field></div></section>
        <section className="vehicle-section"><h2><Gauge size={18} /> Especificaciones</h2><div className="vehicle-field-grid"><Field label="Motor">{value(vehicle.engineDescription ?? vehicle.engineSizeLiters)}</Field><Field label="Potencia">{vehicle.engineHorsepower === null ? "No reportada" : `${vehicle.engineHorsepower} HP`}</Field><Field label="Cilindros">{value(vehicle.cylinders)}</Field><Field label="Tipo de motor">{value(vehicle.engineLayout)}</Field><Field label="Combustible"><Fuel size={12} /> {value(vehicle.fuelType)}</Field><Field label="Transmisión">{value(vehicle.transmission)}</Field><Field label="Tracción">{value(vehicle.driveType)}</Field><Field label="Llave"><KeyRound size={12} /> {value(vehicle.hasKey)}</Field><Field label="Airbags">{value(vehicle.airbags)}</Field><Field label="Restricción">{value(vehicle.restraintSystem)}</Field></div></section>
        <section className="vehicle-section"><h2><Wrench size={18} /> Condición</h2><div className="vehicle-field-grid"><Field label="Daño primario">{value(vehicle.damage)}</Field><Field label="Daño secundario">{value(vehicle.secondaryDamage)}</Field><Field label="Loss type">{value(vehicle.lossType)}</Field><Field label="Run & Drive">{value(vehicle.startCode)}</Field><Field label="Color"><Palette size={12} /> {value(vehicle.color)}</Field><Field label="Score">{value(vehicle.vehicleScore)}</Field></div><div className="vehicle-risk-note"><CircleAlert size={16} /> La descripción de daños procede del feed y no sustituye una inspección mecánica, estructural o documental.</div></section>
        <section className="vehicle-section"><h2><FileCheck2 size={18} /> Documento y vendedor</h2><div className="vehicle-field-grid"><Field label="Documento">{value(vehicle.titleType)}</Field><Field label="Tipo">{value(vehicle.saleDocumentType)}</Field><Field label="Grupo">{value(vehicle.saleDocumentGroup)}</Field><Field label="Pendiente">{value(vehicle.saleDocumentPending)}</Field><Field label="Exportable">{value(vehicle.saleDocumentExport)}</Field><Field label="Registrable">{value(vehicle.saleDocumentRegistration)}</Field><Field label="Title brand">{value(vehicle.titleBrand)}</Field><Field label="Notas de título">{value(vehicle.titleNotes)}</Field><Field label="Vendedor">{value(vehicle.sellerName)}</Field><Field label="Tipo vendedor">{value(vehicle.sellerType)}</Field></div></section>
        <section className="vehicle-section vehicle-section--wide"><h2><Building2 size={18} /> Información de venta y trazabilidad</h2><div className="vehicle-field-grid"><Field label="Actual cash value">{money(vehicle.actualCashValueUsd)}</Field><Field label="Costo estimado reparación">{money(vehicle.estimatedRepairCostUsd)}</Field><Field label="Send from">{value(vehicle.sendFrom)}</Field><Field label="Selling branch">{value(vehicle.sellingBranch)}</Field><Field label="Facility ID">{value(vehicle.facilityId)}</Field><Field label="Estado">{value(vehicle.state)}</Field><Field label="Lane">{value(vehicle.lane)}</Field><Field label="Aisle">{value(vehicle.aisle)}</Field><Field label="Clase">{value(vehicle.vehicleClass)}</Field><Field label="Opciones">{value(vehicle.options)}</Field><Field label="Última observación">{vehicle.observedAt ? new Date(vehicle.observedAt).toLocaleString("es-US") : "No reportada"}</Field><Field label="Media">{gallery.length} foto{gallery.length === 1 ? "" : "s"}{vehicle.has360 ? " · 360°" : ""}{vehicle.hasVideo ? " · video" : ""}</Field></div></section>
      </div>
      <footer className="vehicle-detail-footer">Azure Inventory Engine · PostgreSQL y Blob privados · La Subasta Cubana</footer>
    </div>
  </main>;
}
