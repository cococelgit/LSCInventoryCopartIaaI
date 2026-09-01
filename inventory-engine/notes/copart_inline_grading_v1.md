# Copart Inline Grading v1

## Objetivo

Todo lote **Copart** que supera la evaluación existente `AuctionEligibilityEvaluator.Evaluate(...)` recibe el resultado determinista del único motor `LscVehicleScoringEngine` antes de que el snapshot y la proyección de inventario se confirmen en PostgreSQL. La política es `lsc_pre_grade_v1`; esta integración no duplica ni modifica su fórmula.

El alcance es exclusivamente la ingesta Copart desde CSV/CSV.GZ ya descargado en Azure. IAAI continúa por Apibara/Open y conserva su ruta de persistencia/cola de scoring existente.

## Orden por fila

```text
CSV Copart
  → adaptación y limpieza canónica
  → evaluación de elegibilidad + auditoría
  → si LoadToSystem = false: descarte/cuarentena auditado, sin scoring inline
  → si LoadToSystem = true: cálculo canónico de scoring
  → transacción PostgreSQL: snapshot + versión + lifecycle + score actual/histórico
  → commit; el lote queda disponible para la proyección de lectura
```

Los lotes con decisión `MARCAR` siguen cargándose. El motor puede devolver `MANUAL_REVIEW`, `NEEDS_ENRICHMENT` o `PRE_GRADED` según su política y datos disponibles. No se inventa un score cuando la información de la subasta es insuficiente.

## Persistencia e idempotencia

`IInventorySnapshotStore.PersistCopartAcceptedWithScoringAsync(...)` es el contrato Copart-only. Su implementación PostgreSQL hace el upsert de `auction_lots`, la inserción idempotente de `auction_lot_versions`, la reactivación de lifecycle y la persistencia del score actual/histórico dentro de una sola transacción.

La verificación de idempotencia usa la política canónica y `input_hash`. Si el score actual tiene el mismo `policy_version` e `input_hash`, no recalcula el motor y preserva `scored_at`; solo adelanta `source_observed_at` para indicar que el mismo insumo se volvió a observar. Ese caso se registra como `scoreSkippedUnchanged`.

Si una operación de snapshot o score falla, la transacción no confirma una proyección nueva sin score. La fila se registra como error de procesamiento; el snapshot no se considera completo y la reconciliación queda bloqueada, preservando el estado previo coherente. El blob privado de auditoría puede existir antes del intento de transacción, pero no queda referenciado por una versión publicada si el commit falla.

## Métricas de corrida

Las nuevas métricas se guardan solamente en `copart_snapshot_manifests`. Las filas históricas quedan en `NULL`, que debe mostrarse como **N/D**, no como cero.

| Columna | Semántica |
|---|---|
| `created_count` | Lotes aceptados cuyo `lot_key` no existía antes de la fila. |
| `updated_count` | Lotes existentes con un `payload_hash` nuevo. |
| `unchanged_count` | Lotes existentes cuyo `payload_hash` ya existía. |
| `scored_inline_count` | Lotes cuyo motor canónico fue evaluado y persistido dentro de la corrida. |
| `score_skipped_unchanged_count` | Lotes con score vigente de misma política e `input_hash`; no se recalculó ni cambió `scored_at`. |
| `score_failed_count` | Filas elegibles cuyo snapshot + scoring no pudo confirmarse. |
| `inline_scoring_duration_ms` | Suma de duración del cálculo puro del motor, sin red/base de datos. |
| `inline_scoring_p50_ms` / `inline_scoring_p95_ms` | Percentiles sobre cálculos de score realizados, o `NULL` cuando no se calculó ningún score. |

Los campos se incluyen en el reporte interno `GetCopartPublicationReportAsync`. La exposición pública del Centro de Ejecuciones corresponde al agente de API/portal; no se añadió ni desplegó un endpoint público en este cambio.

## Fotos HD: alcance separado

El grading en línea no depende de fotos, por diseño de `lsc_pre_grade_v1`. La ingesta conserva miniatura y datos de galería disponibles. El enriquecimiento HD continúa aislado en `CopartMediaEnrichmentProcessor` con `--copart-media-enrich`; no fue incorporado al cron, no se ejecutó y no bloquea una carga Copart válida.

El trabajo futuro de fotos debe tener release separado, rate limit, reintentos y métricas `thumbnailOnly`, `galleryResolved`, `media404` y `mediaResolutionFailed`. No debe bloquear scoring ni reconciliación.

## Límites de este cambio

No se modifican ni ejecutan IAAI, Apibara, `job-lsc-inventory-scoring-prod`, cron, secretos, identidades, Redis, Facets V2, Jobs Copart existentes ni despliegues Azure. No se ejecutó backfill de scoring ni una carga manual de producción.

## Validación local

Las pruebas cubren un lote Copart elegible con score canónico, un lote `MARCAR` con score de revisión, re-proceso con el mismo hash, cambio de insumo relevante, descarte sin score y fallo de persistencia que bloquea reconciliación. La suite .NET completa y `pnpm check` deben aprobar antes de cualquier release.

## Backfill manual de Copart existente

El comando explícito `--copart-scoring-backfill` recupera resultados para lotes **Copart activos** cuyo score actual no existe, usa otra política o no corresponde al `observed_at` actual. Se ejecuta exclusivamente desde el workflow del job `job-lsc-copart-excel-prod` mediante el modo `scoring_backfill`.

Trabaja por bloques de 500 lotes y con concurrencia máxima de 8, configurables solo con `CopartExcel__ScoringBackfillBatchSize` y `CopartExcel__ScoringBackfillConcurrency`. Para cada candidato, vuelve a aplicar la elegibilidad determinista a su snapshot persistido y escribe únicamente el resultado canónico de scoring. No descarga Excel, no cambia `auction_lots`, `auction_lot_versions`, lifecycle, media, auditoría de elegibilidad, títulos, IAAI ni `inventory_vehicle_scoring_queue`.

Si una fila no puede puntuarse, el resultado registra un fallo sanitizado, termina sin bucle ocupado y la deja pendiente para una recuperación explícita posterior. El resumen se registra como `copart-scoring-backfill` en `inventory_sync_runs`.
