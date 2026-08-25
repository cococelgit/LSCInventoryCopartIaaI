/**
 * Style reminder — Tablero de torre de control: precisión sobria, evidencia visible,
 * azul petróleo profundo, jade para controles validados y ámbar para datos incompletos.
 */
import { useMemo, useState } from "react";
import {
  Activity,
  ArrowUpRight,
  BadgeCheck,
  BarChart3,
  CarFront,
  Check,
  ChevronRight,
  CircleAlert,
  ClipboardCheck,
  Database,
  Eye,
  FileLock2,
  Gauge,
  Layers3,
  LockKeyhole,
  Menu,
  Radar,
  ShieldCheck,
  Sparkles,
  X,
} from "lucide-react";

type View = "resumen" | "calidad" | "lotes";

const lots = [
  { lot: "48841576", vehicle: "2012 Chevrolet Malibu 2LT", bid: "—", photos: 1, status: "Sin puja actual" },
  { lot: "50566696", vehicle: "2004 Workhorse P42 Delivery Truck", bid: "$450", photos: 13, status: "En seguimiento" },
  { lot: "52483876", vehicle: "2019 Ford Fiesta SE", bid: "$450", photos: 1, status: "En seguimiento" },
  { lot: "53629776", vehicle: "2025 Chevrolet Silverado C1500 RST", bid: "$14,600", photos: 12, status: "En seguimiento" },
  { lot: "54292856", vehicle: "2025 Dodge Charger Daytona R", bid: "$5,900", photos: 12, status: "En seguimiento" },
];

const quality = [
  { label: "VIN", value: "100%", note: "24 de 24", tone: "good" },
  { label: "Título", value: "100%", note: "24 de 24", tone: "good" },
  { label: "Fecha de subasta", value: "100%", note: "24 de 24", tone: "good" },
  { label: "Fotos declaradas", value: "100%", note: "24 de 24", tone: "good" },
  { label: "Puja actual", value: "62.5%", note: "15 de 24", tone: "warn" },
  { label: "Daño", value: "0%", note: "No disponible", tone: "mute" },
  { label: "Odómetro", value: "0%", note: "No disponible", tone: "mute" },
];

function Signal({ label, tone = "verified" }: { label: string; tone?: "verified" | "caution" | "neutral" }) {
  return (
    <span className={`signal signal--${tone}`}>
      <i />
      {label}
    </span>
  );
}

function Metric({ icon: Icon, value, label, detail, tone = "jade" }: { icon: typeof Database; value: string; label: string; detail: string; tone?: "jade" | "amber" | "blue" | "slate" }) {
  return (
    <article className="metric-card">
      <div className={`metric-icon metric-icon--${tone}`}><Icon size={19} strokeWidth={1.8} /></div>
      <p className="metric-label">{label}</p>
      <strong>{value}</strong>
      <span>{detail}</span>
    </article>
  );
}

function SectionHeader({ eyebrow, title, copy }: { eyebrow: string; title: string; copy: string }) {
  return (
    <div className="section-heading">
      <p>{eyebrow}</p>
      <h2>{title}</h2>
      <span>{copy}</span>
    </div>
  );
}

export default function Home() {
  const [view, setView] = useState<View>("resumen");
  const [navOpen, setNavOpen] = useState(false);
  const nav = useMemo(() => [
    { id: "resumen" as const, label: "Resumen operativo", icon: Radar },
    { id: "calidad" as const, label: "Calidad del feed", icon: BarChart3 },
    { id: "lotes" as const, label: "Lotes verificados", icon: Layers3 },
  ], []);

  const switchView = (next: View) => {
    setView(next);
    setNavOpen(false);
  };

  return (
    <main className="app-shell">
      <aside className={`control-rail ${navOpen ? "control-rail--open" : ""}`}>
        <div className="rail-top">
          <div className="brand-lockup">
            <img src="/manus-storage/lsc-inventory-control-logo_1205ca6b.png" alt="Símbolo de La Subasta Cubana" />
            <span><b>LA SUBASTA CUBANA</b><em>INVENTORY REVIEW</em></span>
          </div>
          <button className="mobile-close" onClick={() => setNavOpen(false)} aria-label="Cerrar menú"><X size={18} /></button>
        </div>

        <div className="rail-label">PANEL DE REVISIÓN</div>
        <nav>
          {nav.map(({ id, label, icon: Icon }) => (
            <button key={id} onClick={() => switchView(id)} className={view === id ? "nav-item nav-item--active" : "nav-item"}>
              <Icon size={17} strokeWidth={1.8} />
              <span>{label}</span>
              {view === id && <ChevronRight size={15} />}
            </button>
          ))}
        </nav>

        <div className="rail-bottom">
          <div className="security-mini">
            <LockKeyhole size={16} />
            <div><b>Vista privada</b><span>Solo lectura</span></div>
          </div>
          <p>Snapshot validado<br />25 AGO 2026 · 05:59 UTC</p>
        </div>
      </aside>

      <section className="workspace">
        <header className="topbar">
          <button className="mobile-menu" onClick={() => setNavOpen(true)} aria-label="Abrir menú"><Menu size={20} /></button>
          <div className="breadcrumb"><span>CONTROL ROOM</span><ChevronRight size={14} /><b>INVENTORY ENGINE</b></div>
          <div className="topbar-right"><span className="build-stamp">IR · MV-0.1</span><span className="divider" /><Signal label="ENTORNO PRIVADO" /><span className="divider" /><span className="readonly"><Eye size={14} /> SOLO LECTURA</span></div>
        </header>

        <section className="situation-strip">
          <div className="situation-copy">
            <div className="eyebrow"><Activity size={14} /> ESTADO DEL PILOTO</div>
            <h1>Inventario validado,<br /><i>no prometido.</i></h1>
            <p>Copart Florida · corte operativo limitado · evidencia guardada de forma privada.</p>
            <div className="situation-signals"><Signal label="SINCRONIZACIÓN MANUAL" /><Signal label="0 FALLOS" tone="verified" /></div>
          </div>
          <div className="situation-orb" aria-hidden="true">
            <span className="orb-ring orb-ring--one" /><span className="orb-ring orb-ring--two" /><span className="orb-core"><ShieldCheck size={34} /></span>
          </div>
          <div className="situation-foot"><span>ÚLTIMA EJECUCIÓN</span><b>20 vehículos</b><small>4 solicitudes lógicas</small></div>
        </section>

        <div className="content-wrap">
          <div className="pulse-band" aria-label="Trazabilidad operativa activa">
            <div className="pulse-band__tag"><i /> CANAL DE EVIDENCIA ACTIVO</div>
            <div className="pulse-band__track"><span /><span /><span /><b /></div>
            <div className="pulse-band__scope">COPART <em>/</em> FLORIDA <em>/</em> PRIVADO</div>
          </div>
          {view === "resumen" && (
            <>
              <div className="metrics-grid stagger">
                <Metric icon={CarFront} value="24" label="Lotes únicos" detail="Inventario consolidado" tone="jade" />
                <Metric icon={Database} value="26" label="Versiones auditables" detail="Cambios preservados" tone="blue" />
                <Metric icon={ClipboardCheck} value="100%" label="VIN y título" detail="Cobertura de la muestra" tone="jade" />
                <Metric icon={Gauge} value="62.5%" label="Puja actual" detail="Campo disponible" tone="amber" />
              </div>

              <div className="overview-grid">
                <article className="panel feed-panel">
                  <SectionHeader eyebrow="SALUD DEL FEED" title="Cobertura que sí llegó" copy="La ausencia se declara. No se rellena ni se infiere." />
                  <div className="signal-lines">
                    {quality.slice(0, 5).map((item) => (
                      <div className="quality-line" key={item.label}>
                        <div><span>{item.label}</span><b>{item.note}</b></div>
                        <div className="bar"><i className={`bar-fill bar-fill--${item.tone}`} style={{ width: item.value }} /></div>
                        <strong>{item.value}</strong>
                      </div>
                    ))}
                  </div>
                  <button className="inspect-link" onClick={() => switchView("calidad")}>Inspeccionar calidad completa <ArrowUpRight size={15} /></button>
                </article>

                <article className="panel protocol-panel">
                  <div className="protocol-img" />
                  <div className="protocol-content">
                    <div className="mini-heading"><FileLock2 size={14} /> PROTOCOLO ACTIVO</div>
                    <h3>El sistema registra la evidencia; la decisión sigue siendo humana.</h3>
                    <ul>
                      <li><Check size={15} /> PostgreSQL y Blob privados</li>
                      <li><Check size={15} /> Identidad de mínimo privilegio</li>
                      <li><Check size={15} /> Sin polling ni pujas automáticas</li>
                    </ul>
                  </div>
                </article>
              </div>
            </>
          )}

          {view === "calidad" && (
            <section className="panel quality-panel stagger">
              <SectionHeader eyebrow="COBERTURA DE CAMPOS" title="Calidad del feed actual" copy="Resultados sobre 24 lotes únicos persistidos en el corte validado." />
              <div className="quality-grid">
                {quality.map((item) => (
                  <article className={`quality-tile quality-tile--${item.tone}`} key={item.label}>
                    <span>{item.label}</span><b>{item.value}</b><small>{item.note}</small>
                    <div><i style={{ width: item.value }} /></div>
                  </article>
                ))}
              </div>
              <div className="caveat"><CircleAlert size={18} /><p><b>Límite actual:</b> daño y odómetro no llegaron en esta muestra. Esta vista los marca como ausentes; no genera una evaluación mecánica ni una recomendación de compra.</p></div>
            </section>
          )}

          {view === "lotes" && (
            <section className="panel lots-panel stagger">
              <SectionHeader eyebrow="MUESTRA SANITIZADA" title="Lotes verificados" copy="Se muestran identificadores de lote y datos operativos. Los VIN completos y payloads permanecen privados." />
              <div className="lots-table-wrap">
                <table>
                  <thead><tr><th>Lote</th><th>Vehículo</th><th>Puja actual</th><th>Fotos</th><th>Estado</th></tr></thead>
                  <tbody>{lots.map((lot) => <tr key={lot.lot}><td><code>#{lot.lot}</code></td><td>{lot.vehicle}</td><td className={lot.bid === "—" ? "missing" : "bid"}>{lot.bid}</td><td>{lot.photos}</td><td><span className={lot.bid === "—" ? "lot-status lot-status--neutral" : "lot-status"}>{lot.status}</span></td></tr>)}</tbody>
                </table>
              </div>
            </section>
          )}

          <footer className="data-footer">
            <span><Sparkles size={15} /> Corte operacional, no catálogo comercial</span>
            <span>La Subasta Cubana · Inventory Engine MVP</span>
          </footer>
        </div>
      </section>
    </main>
  );
}
