/**
 * Style reminder — Ficha de evidencia en azul, blanco y rojo: lectura limpia, detalles trazables y ningún dato inferido.
 */
import { ArrowLeft, CalendarDays, CarFront, CircleAlert, FileCheck2, Image, MapPin, ShieldCheck } from "lucide-react";
import { formatMoney, vehicleFromLot } from "../data/inventory";

export default function VehicleDetail({ params }: { params: { lot: string } }) {
  const vehicle = vehicleFromLot(params.lot);
  if (!vehicle) return <main className="detail-page"><section className="detail-not-found"><CarFront size={42} /><h1>Lote no disponible en este corte.</h1><a href="/">Volver al inventario</a></section></main>;
  return <main className="detail-page">
    <header className="detail-header"><a className="detail-brand" href="/"><img src="/manus-storage/lsc-inventory-control-logo_1205ca6b.png" alt="La Subasta Cubana" /><span>LA SUBASTA CUBANA <em>INVENTORY</em></span></a><span><ShieldCheck size={16} /> FICHA INTERNA · SOLO LECTURA</span></header>
    <section className="detail-hero"><a href="/" className="back-link"><ArrowLeft size={16} /> Volver al listado</a><div className="detail-visual"><div><CarFront size={108} strokeWidth={1.05} /><span>MEDIA · EVIDENCIA NO DESCARGADA</span><b><i /> SOLO METADATOS DEL FEED</b></div></div><div className="detail-heading"><p>LOTE #{vehicle.lot} <i /> {vehicle.availability.toUpperCase()}</p><h1>{vehicle.title}</h1><div className="detail-facts"><span><CalendarDays size={15} /> {vehicle.auctionDate}</span><span><Image size={15} /> {vehicle.photos} foto{vehicle.photos !== 1 ? "s" : ""} declarada{vehicle.photos !== 1 ? "s" : ""}</span><span><MapPin size={15} /> Florida</span></div></div></section>
    <div className="detail-grid"><aside className="detail-rail"><p><span className="seal-dot" /> ESTADO DE LECTURA</p><b>VALIDADO</b><span>Snapshot auditado</span><hr /><small>COPART · FLORIDA</small><small>SOLO LECTURA</small><small>25 AGO 2026 · 05:59 UTC</small></aside><section className="detail-card price-card"><span>PUJA ACTUAL REPORTADA</span><b className={vehicle.currentBid === null ? "missing-price" : ""}>{formatMoney(vehicle.currentBid)}</b><small>{vehicle.currentBid === null ? "El feed no incluyó una puja para este lote." : "Campo recibido en la última lectura del feed."}</small></section><section className="detail-card"><div className="card-title"><FileCheck2 size={18} /> Campos recibidos</div><div className="field-list"><span><b>VIN</b><em>Disponible de forma privada</em></span><span><b>Título</b><em>Disponible</em></span><span><b>Fecha de subasta</b><em>Disponible</em></span><span><b>Fotos declaradas</b><em>Disponible</em></span></div></section><section className="detail-card caution-card"><div className="card-title"><CircleAlert size={18} /> Campos no recibidos</div><div className="field-list"><span><b>Daño</b><em>No disponible</em></span><span><b>Odómetro</b><em>No disponible</em></span></div><p>La ausencia se muestra tal cual. Esta ficha no constituye una inspección mecánica ni una recomendación de compra.</p></section></div>
    <footer className="detail-footer">Corte validado · PostgreSQL y auditoría privados · La Subasta Cubana</footer>
  </main>;
}
