# Sprint 1 — Historial de intentos y posible vendedor motivado

**Autor:** Manus AI

**Fecha:** 8 de septiembre de 2026

**Rama:** `manus/motivated-seller-sprint1`
**Decisión:** **Sprint 1 aprobado técnicamente; producción permanece bloqueada.**

## Resumen ejecutivo

Se implementó el núcleo aislado que convierte `prices[]` de AuctionsAPI en intentos de venta tipados, idempotentes y auditables. El módulo calcula una señal versionada separada del Score LSC, distingue historial no solicitado de historial vacío y nunca afirma como hecho que el vendedor está motivado. AuctionsAPI documenta `prices_history=1`, y los cuatro casos previamente confirmados se conservaron como fixtures sanitizados de regresión.[1]

> El Sprint 1 no modifica PostgreSQL productivo, jobs, cron, backfill ni UI. Los tres flags nuevos permanecen apagados por defecto.

| Control | Resultado |
|---|---:|
| Suite completa del Inventory Engine | 258/258 pruebas exitosas |
| Compilación Release | 0 errores; 0 warnings |
| Migración en PostgreSQL local aislado | Aplicada dos veces sin duplicar objetos |
| Prueba end-to-end PostgreSQL | Persistencia, lectura y replay idempotente exitosos |
| Dry-run real | 20 IAAI + 20 Copart, ejecutado dos veces |
| Fingerprint entre dry-runs | Idéntico |
| Escrituras PostgreSQL en dry-run | 0 |
| Jobs o schedules modificados | 0 |

## Entregables implementados

El contrato tolerante normaliza IDs, fechas, estados, pujas y Buy Now históricos. La llave preferida es el `prices[].id` del proveedor; si falta, se usa un hash determinístico de plataforma, lote, fecha, estado y precios. Los importes cero se conservan como ausencia de precio, no como una oferta real.

| Componente | Implementación |
|---|---|
| Parser | `AuctionsApiSaleAttemptParser` |
| Modelo histórico | `AuctionSaleAttempt` y `AuctionSaleHistorySnapshot` |
| Cálculo | `SellerMotivationSignalCalculator`, política `motivated_seller_v1` |
| Gate operativo | `Enabled`, `AllowWrites`, `IncludePricesHistory`; todos `false` |
| Historial PostgreSQL | `inventory_sale_attempts` |
| Read model futuro | `inventory_sale_attempt_signals_current` |
| Persistencia | Upsert transaccional con orden por `source_observed_at` |
| Dry-run reproducible | `tools/Lsc.Inventory.SaleAttemptDryRun` |

Las tablas son aditivas y el SQL no contiene `DROP` ni `TRUNCATE`. La migración revisable y el SQL embebido están cubiertos por una prueba que impide que diverjan.[2]

## Cuatro casos de regresión

| Plataforma / lote | No ventas | Máxima puja histórica | Ask vigente | Resultado v1 |
|---|---:|---:|---:|---|
| Copart `54450906` | 26 | $49,750 | $47,362 | Alta |
| Copart `53371186` | 19 | $2,800 | $2,000 | Alta |
| IAAI `45785276` | 5 | $1,500 | $1,500 | Alta |
| IAAI `37887931` | 4 | $2,150 | No disponible | Seguimiento |

El último caso es deliberado: sin precio actual no se debe fabricar una brecha ni elevar la señal a Alta o Media.

## Dry-run 20 + 20

La misma muestra read-only se procesó dos veces. Ambos reportes produjeron el fingerprint `cc18a5c0e094eef249fd85d35161425913855b298cbaa51e3827412de426cfe0`, confirmando determinismo para la misma fuente dentro de la misma fecha de cálculo.[3] [4]

| Plataforma | Lotes | Con historial | Intentos | Alta | Media | Seguimiento | Segundo replay |
|---|---:|---:|---:|---:|---:|---:|---|
| IAAI | 20 | 6 | 11 | 0 | 0 | 3 | 11 sin cambio |
| Copart | 20 | 1 | 2 | 0 | 0 | 1 | 2 sin cambio |

La muestra pequeña no encontró señales Alta o Media. Esto no contradice los cuatro fixtures conocidos; confirma que la señal será selectiva y que la cobertura histórica varía según los lotes recientes.

## Controles cerrados

El módulo no está conectado a `AuctionsApiIncrementalSyncProcessor`, `AuctionsApiInitialImportProcessor` ni ningún hosted service. El parámetro `prices_history=1` solo aparece cuando una llamada lo solicita explícitamente. Aunque `AuctionsApi:AllowWrites` esté activo en producción, el nuevo repositorio exige además `SaleAttemptIntelligence:Enabled=true` y `SaleAttemptIntelligence:AllowWrites=true`.

El estado actual `sold` o `archived=true` suprime la señal. `not_sold` se cuenta únicamente desde intentos históricos válidos con `sale_date`. El hash incluye política, estado actual, precios, intentos y fecha UTC del cálculo para que `days_in_cycle` pueda avanzar diariamente sin cambiar la política.

## Go/no-go

| Paso siguiente | Decisión |
|---|---|
| Fusionar código aislado después de revisión | **GO** |
| Crear tablas en PostgreSQL productivo | **NO-GO todavía** |
| Activar `prices_history` en jobs | **NO-GO todavía** |
| Piloto productivo 100 IAAI + 100 Copart | **Pendiente de Sprint 2 y autorización** |
| Backfill completo | **No autorizado** |
| UI, filtros, badge o timeline | **Fuera de alcance** |

Antes de producción, el Sprint 2 debe revisar el pull request, aplicar la migración aditiva en una ventana controlada, habilitar primero solo lectura/cálculo, ejecutar el piloto 100 + 100 y medir cinco corridas por plataforma. Solo después debe evaluarse `AllowWrites=true` para este módulo.

## References

[1]: https://auctionsapi.com/auction-docs "AuctionsAPI — Auction Data API Reference"
[2]: ../infra/sql/015_sale_attempt_intelligence_v1.sql "Migración aditiva del historial de intentos"
[3]: ../artifacts/sale-attempt-sprint1/run-1.json "Dry-run real 1 — 20 IAAI + 20 Copart"
[4]: ../artifacts/sale-attempt-sprint1/run-2.json "Dry-run real 2 — 20 IAAI + 20 Copart"
