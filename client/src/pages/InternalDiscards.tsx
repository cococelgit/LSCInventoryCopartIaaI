import { useAuth } from "@/_core/hooks/useAuth";
import DashboardLayout from "@/components/DashboardLayout";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { trpc } from "@/lib/trpc";
import { AlertTriangle, ArrowLeft, ChevronLeft, ChevronRight, FileWarning, Search, ShieldX } from "lucide-react";
import React, { FormEvent, useEffect, useState } from "react";

const PAGE_SIZE = 25;

export default function InternalDiscards() {
  const { user, loading } = useAuth();
  const [page, setPage] = useState(1);
  const [selectedRule, setSelectedRule] = useState("");
  const [queryDraft, setQueryDraft] = useState("");
  const [query, setQuery] = useState("");
  const audit = trpc.internalAudit.discarded.useQuery(
    { page, pageSize: PAGE_SIZE, ruleCode: selectedRule || undefined, query: query || undefined },
    { enabled: user?.role === "admin", staleTime: 30_000, refetchOnWindowFocus: true },
  );

  useEffect(() => setPage(1), [selectedRule, query]);

  const submitSearch = (event: FormEvent) => {
    event.preventDefault();
    setQuery(queryDraft.trim());
  };

  return <DashboardLayout>
    {loading ? null : user?.role !== "admin" ? <section className="mx-auto mt-20 max-w-xl rounded-2xl border border-red-200 bg-white p-8 text-center shadow-sm">
      <ShieldX className="mx-auto mb-4 h-10 w-10 text-red-600" />
      <h1 className="text-2xl font-bold text-slate-900">Acceso administrativo requerido</h1>
      <p className="mt-2 text-sm leading-6 text-slate-600">Este panel contiene decisiones internas de elegibilidad. Solicita acceso de administrador al propietario del sistema.</p>
      <a href="/" className="mt-6 inline-flex items-center gap-2 text-sm font-bold text-blue-700"><ArrowLeft className="h-4 w-4" /> Volver al inventario</a>
    </section> : <div className="mx-auto max-w-[1500px] space-y-6">
      <header className="flex flex-col gap-4 rounded-2xl bg-[#031b86] p-6 text-white shadow-xl shadow-blue-950/10 sm:flex-row sm:items-end sm:justify-between">
        <div>
          <p className="text-xs font-bold uppercase tracking-[0.18em] text-blue-200">Auditoría interna</p>
          <h1 className="mt-2 text-3xl font-bold tracking-tight">Vehículos descartados</h1>
          <p className="mt-2 max-w-2xl text-sm leading-6 text-blue-100">Decisiones aplicadas antes de PostgreSQL. Cada lote conserva la regla, evidencia observada, versión y fecha de evaluación.</p>
        </div>
        <a href="/" className="inline-flex h-10 items-center justify-center gap-2 rounded-lg border border-white/25 bg-white/10 px-4 text-sm font-bold transition hover:bg-white/20"><ArrowLeft className="h-4 w-4" /> Inventario</a>
      </header>

      <section className="grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
        <button onClick={() => setSelectedRule("")} className={`rounded-xl border bg-white p-4 text-left shadow-sm transition ${selectedRule === "" ? "border-blue-600 ring-2 ring-blue-100" : "border-slate-200 hover:border-blue-300"}`}>
          <span className="text-xs font-bold uppercase tracking-wider text-slate-500">Total descartados</span>
          <strong className="mt-2 block text-3xl text-slate-950">{audit.data?.total ?? "—"}</strong>
        </button>
        {(audit.data?.ruleSummary ?? []).map((rule) => <button key={rule.code} onClick={() => setSelectedRule(rule.code)} className={`rounded-xl border bg-white p-4 text-left shadow-sm transition ${selectedRule === rule.code ? "border-red-500 ring-2 ring-red-100" : "border-slate-200 hover:border-red-300"}`}>
          <span className="inline-flex rounded bg-red-50 px-2 py-1 text-xs font-black text-red-700">{rule.code}</span>
          <strong className="mt-2 block text-2xl text-slate-950">{rule.count}</strong>
          <small className="mt-1 block truncate text-slate-500">{rule.name}</small>
        </button>)}
      </section>

      <section className="rounded-2xl border border-slate-200 bg-white shadow-sm">
        <div className="flex flex-col gap-3 border-b border-slate-200 p-4 lg:flex-row lg:items-center lg:justify-between">
          <div>
            <h2 className="flex items-center gap-2 text-lg font-bold text-slate-950"><FileWarning className="h-5 w-5 text-red-600" /> Registro de decisiones</h2>
            <p className="mt-1 text-xs text-slate-500">VIN enmascarado; evidencia limitada a los campos usados por la regla.</p>
          </div>
          <form onSubmit={submitSearch} className="flex w-full max-w-md gap-2">
            <div className="relative flex-1"><Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" /><Input value={queryDraft} onChange={(event) => setQueryDraft(event.target.value)} className="pl-9" placeholder="Buscar lote o últimos 4 del VIN" aria-label="Buscar descartes" /></div>
            <Button type="submit">Buscar</Button>
          </form>
        </div>

        {audit.isLoading ? <div className="p-12 text-center text-sm text-slate-500">Cargando decisiones auditadas…</div> : audit.isError ? <div className="m-5 flex gap-3 rounded-xl border border-red-200 bg-red-50 p-5 text-red-900"><AlertTriangle className="mt-0.5 h-5 w-5 shrink-0" /><div><b>No se pudo consultar la auditoría.</b><p className="mt-1 text-sm">Verifica tu sesión administrativa e inténtalo nuevamente.</p></div></div> : (audit.data?.items.length ?? 0) === 0 ? <div className="p-12 text-center"><ShieldX className="mx-auto h-9 w-9 text-slate-300" /><h3 className="mt-3 font-bold text-slate-800">No hay descartes para este filtro</h3><p className="mt-1 text-sm text-slate-500">Cambia la regla o limpia la búsqueda.</p></div> : <div className="overflow-x-auto">
          <table className="w-full min-w-[980px] text-left text-sm">
            <thead className="bg-slate-50 text-xs uppercase tracking-wider text-slate-500"><tr><th className="px-5 py-3">Evaluado</th><th className="px-5 py-3">Lote</th><th className="px-5 py-3">VIN</th><th className="px-5 py-3">Regla</th><th className="px-5 py-3">Motivo</th><th className="px-5 py-3">Evidencia</th><th className="px-5 py-3">Versión</th></tr></thead>
            <tbody className="divide-y divide-slate-100">{audit.data?.items.map((item) => {
              const reason = item.evaluation.discard_reasons[0];
              return <tr key={`${item.evaluation.lot_number}-${item.evaluatedAt}`} className="align-top hover:bg-slate-50/80">
                <td className="whitespace-nowrap px-5 py-4 text-xs text-slate-500">{new Date(item.evaluatedAt).toLocaleString("es-US")}</td>
                <td className="px-5 py-4 font-bold text-slate-950">#{item.evaluation.lot_number ?? "N/R"}</td>
                <td className="px-5 py-4 font-mono text-xs text-slate-600">{item.evaluation.vin_masked ?? "N/R"}</td>
                <td className="px-5 py-4"><span className="rounded bg-red-50 px-2 py-1 text-xs font-black text-red-700">{reason?.code ?? "N/R"}</span></td>
                <td className="max-w-xs px-5 py-4"><b className="block text-slate-900">{reason?.name ?? "Sin motivo"}</b><span className="mt-1 block text-xs leading-5 text-slate-500">{reason?.explanation}</span></td>
                <td className="max-w-sm px-5 py-4"><details><summary className="cursor-pointer text-xs font-bold text-blue-700">Ver campos observados</summary><pre className="mt-2 max-w-sm overflow-auto rounded-lg bg-slate-950 p-3 text-[11px] leading-5 text-slate-100">{JSON.stringify(reason?.observed_values ?? {}, null, 2)}</pre></details></td>
                <td className="whitespace-nowrap px-5 py-4 text-xs text-slate-500">{item.evaluation.rule_version}</td>
              </tr>;
            })}</tbody>
          </table>
        </div>}

        {audit.data && audit.data.total > 0 && <footer className="flex flex-col gap-3 border-t border-slate-200 p-4 sm:flex-row sm:items-center sm:justify-between">
          <span className="text-xs font-semibold text-slate-500">Página {audit.data.page} de {audit.data.totalPages} · {audit.data.total} decisiones</span>
          <div className="flex gap-2"><Button variant="outline" size="sm" disabled={page <= 1} onClick={() => setPage((value) => Math.max(1, value - 1))}><ChevronLeft className="h-4 w-4" /> Anterior</Button><Button variant="outline" size="sm" disabled={page >= audit.data.totalPages} onClick={() => setPage((value) => Math.min(audit.data!.totalPages, value + 1))}>Siguiente <ChevronRight className="h-4 w-4" /></Button></div>
        </footer>}
      </section>
    </div>}
  </DashboardLayout>;
}
