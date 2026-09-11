# Inventory V2 — Fase 2: parity audit V2-only

El parity audit operativo dejó de comparar contra `inventory_search_current`, `auction_lots` y `auction_lot_versions`. El comando `--inventory-v2-parity` ahora ejecuta `GetInventoryV2IntegrityReportAsync`, que valida únicamente las tablas estructuradas V2.

## Checks operativos

| Check | Propósito |
|---|---|
| `V2Rows` | Confirma que existe inventario V2 en el alcance solicitado |
| `ActiveRows` / `InactiveRows` | Verifica el lifecycle actual |
| `RowsMissingIdentity` | Detecta plataforma, lote o clave incompleta |
| `RowsMissingHashes` | Detecta filas que no pueden participar en change detection |
| `MediaCountMismatches` | Compara el contador tipado contra `inventory_media_current_v2` |
| `SellerNeedsReview` | Mide señales pendientes sin bloquear la integridad estructural |
| `PlatformCount` | Confirma cobertura de las plataformas esperadas |
| `Failures` / `IsHealthy` | Entrega decisión machine-readable para el workflow |

La auditoría no consulta JSONB ni la proyección V1. La evidencia histórica del canary V1 versus V2 queda preservada en sus documentos anteriores, pero ya no es necesaria para ejecutar un chequeo operativo después de retirar V1.

La Fase 2 no activa flags productivos, no apaga todavía `LegacyReadFallbackEnabled` y no borra tablas. El audit read-only de Production [34647336786](https://github.com/cococelgit/LSCInventoryCopartIaaI/actions/runs/34647336786) pasó con `IsHealthy=true`: 513 filas V2, 513 activas, 0 inactivas, 0 identidades incompletas, 0 hashes faltantes, 0 discrepancias de media, 255 sellers marcados para revisión y 2 plataformas representadas. Antes de apagar el fallback se debe activar V2 con fallback temporal, observar 24–48 horas y volver a ejecutar el audit con `IsHealthy=true`.
