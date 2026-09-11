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

La Fase 2 no activa flags productivos, no apaga todavía `LegacyReadFallbackEnabled` y no borra tablas. Antes de apagar el fallback se debe ejecutar esta auditoría en Production, activar V2 con fallback temporal, observar 24–48 horas y volver a ejecutar el audit con `IsHealthy=true`.
