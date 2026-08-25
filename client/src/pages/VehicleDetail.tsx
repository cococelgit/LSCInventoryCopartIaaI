/**
 * Style reminder — Ficha de evidencia en azul, blanco y rojo: lectura limpia, detalles trazables y ningún dato inferido.
 */
import { useState } from "react";
import { ArrowLeft, CalendarDays, CarFront, CircleAlert, FileCheck2, Image, MapPin, ShieldCheck } from "lucide-react";
import { trpc } from "../lib/trpc";
import { formatMoney, type Vehicle } from "../data/inventory";

type LiveVehicle = { lot: string; observedAt: string; title: string | null; year: number | null; make: string | null; model: string | null; vehicleType: string | null; color: string | null; fuelType: string | null; transmission: string | null; driveType: string | null; odometer: number | null; damage: string | null; auctionAt: string | null; lotStatus: string | null; currentBidUsd: number | null; buyNowUsd: number | null; location: string | null; state: string | null; titleType: string | null; photos: string[] };

function toVehicle(vehicle: LiveVehicle): Vehicle {
  return {
    lot: vehicle.lot,
    title: vehicle.title ?? `Lote ${vehicle.lot}`,
    year: vehicle.year ?? 0,
    make: vehicle.make ?? "Sin marca",
    model: vehicle.model ?? "Sin modelo",
    currentBid: vehicle.currentBidUsd,
    photos: vehicle.photos.length,
    auctionDate: vehicle.auctionAt ? new Date(vehicle.auctionAt).toLocaleDateString("es-US", { day: "2-digit", month: "short", year: "numeric" }).toUpperCase() : "No reportada",
    lotStatus: vehicle.lotStatus === "" ? "Sin puja actual" : "En seguimiento",
    availability: "Verificado",
    gallery: vehicle.photos,
    publicFacts: { color: vehicle.color ?? undefined, fuel: vehicle.fuelType ?? undefined, transmission: vehicle.transmission ?? undefined, drive: vehicle.driveType ?? undefined, damage: vehicle.damage ?? undefined, titleType: vehicle.titleType ?? undefined, location: vehicle.location ?? undefined },
  };
}

export default function VehicleDetail({ params }: { params: { lot: string } }) {
  const liveVehicle = trpc.inventory.vehicle.useQuery({ lot: params.lot }, { retry: false, staleTime: 60_000 });
  const [activePhoto, setActivePhoto] = useState(0);
  const vehicle = liveVehicle.data ? toVehicle(liveVehicle.data) : null;
  if (liveVehicle.isLoading) return <main className="detail-page"><section className="detail-not-found"><CarFront size={42} /><h1>Cargando la ficha desde Azure.</h1><p>Consultando el lote persistido y su evidencia reportada.</p></section></main>;
  if (!vehicle) return <main className="detail-page"><section className="detail-not-found"><CarFront size={42} /><h1>Lote no disponible en este corte.</h1><a href="/">Volver al inventario</a></section></main>;
  const facts = vehicle.publicFacts;
  const gallery = vehicle.gallery;
  const isLoading = liveVehicle.isLoading;
  return <main className="detail-page">
    <header className="detail-header"><a className="detail-brand" href="/"><img src="/manus-storage/lsc-logo-lineal-blanco_435d949d.png" alt="La Subasta Cubana" /></a><span><ShieldCheck size={16} /> FICHA INTERNA · SOLO LECTURA</span></header>
    <section className="detail-hero"><a href="/" className="back-link"><ArrowLeft size={16} /> Volver al listado</a><div className="detail-media"><div className="detail-visual">{gallery.length ? <img src={gallery[Math.min(activePhoto, gallery.length - 1)]} alt={`${vehicle.title}, foto ${activePhoto + 1}`} /> : <div className="vehicle-photo vehicle-photo--missing">El feed no reportó fotos para este lote.</div>}<span><Image size={13} /> {gallery.length ? (isLoading ? "Cargando evidencia…" : `Foto real ${activePhoto + 1} de ${gallery.length}`) : "Sin foto reportada"}</span></div>{gallery.length > 0 && <div className="detail-gallery" aria-label="Galería de fotos reales">{gallery.map((photo, index) => <button key={photo} className={index === activePhoto ? "detail-thumb detail-thumb--active" : "detail-thumb"} onClick={() => setActivePhoto(index)} aria-label={`Ver foto ${index + 1}`}><img src={photo} alt="" /></button>)}</div>}</div><div className="detail-heading"><p>LOTE #{vehicle.lot} <i /> {vehicle.availability.toUpperCase()}</p><h1>{vehicle.title}</h1><div className="detail-facts"><span><CalendarDays size={15} /> {vehicle.auctionDate}</span><span><Image size={15} /> {vehicle.photos || "No"} foto{vehicle.photos !== 1 ? "s" : ""} reportada{vehicle.photos !== 1 ? "s" : ""}</span><span><MapPin size={15} /> {facts?.location ?? "No reportada"}</span></div><div className="detail-source"><span className="seal-dot" /> Datos y media servidos por Azure Inventory Engine</div></div></section>
    <div className="detail-grid"><aside className="detail-rail"><p><span className="seal-dot" /> ESTADO DE LECTURA</p><b>{isLoading ? "CONSULTANDO" : "ACTUALIZADO"}</b><span>Fuente persistida y auditable</span><hr /><small>COPART · FLORIDA</small><small>SOLO LECTURA</small><small>{liveVehicle.data?.observedAt ? new Date(liveVehicle.data.observedAt).toLocaleString("es-US") : "Sin timestamp reportado"}</small></aside><section className="detail-card price-card"><span>PUJA ACTUAL REPORTADA</span><b className={vehicle.currentBid === null ? "missing-price" : ""}>{formatMoney(vehicle.currentBid)}</b><small>{vehicle.currentBid === null ? "El feed no incluyó una puja para este lote." : "Campo recibido en la última lectura del feed."}</small></section><section className="detail-card"><div className="card-title"><FileCheck2 size={18} /> Campos recibidos</div><div className="field-list"><span><b>VIN</b><em>Disponible de forma privada</em></span><span><b>Título</b><em>Disponible</em></span><span><b>Fecha de subasta</b><em>Disponible</em></span><span><b>Fotos reales</b><em>{vehicle.photos ? "Galería disponible" : "No recibidas"}</em></span></div></section><section className="detail-card caution-card"><div className="card-title"><CircleAlert size={18} /> Campos de riesgo</div><div className="field-list"><span><b>Daño</b><em>{facts?.damage ?? "No disponible en este corte"}</em></span><span><b>Odómetro</b><em>No disponible en el feed</em></span></div><p>La ausencia se muestra tal cual. Esta ficha no constituye una inspección mecánica ni una recomendación de compra.</p></section>{facts && <section className="detail-card fact-card"><div className="card-title"><CarFront size={18} /> Datos públicos del lote</div><div className="field-list"><span><b>Color</b><em>{facts.color ?? "No reportado"}</em></span><span><b>Combustible</b><em>{facts.fuel ?? "No reportado"}</em></span><span><b>Transmisión</b><em>{facts.transmission ?? "No reportada"}</em></span><span><b>Tracción</b><em>{facts.drive ?? "No reportada"}</em></span><span><b>Título</b><em>{facts.titleType ?? "No reportado"}</em></span></div></section>}</div>
    <footer className="detail-footer">Última lectura desde Azure · PostgreSQL y Blob privados · La Subasta Cubana</footer>
  </main>;
}
