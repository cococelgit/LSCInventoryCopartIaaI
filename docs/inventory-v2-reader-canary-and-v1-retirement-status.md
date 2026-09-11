# Inventory V2: canary de lectura y retiro de V1

## Canary de lectura

El canary aislado se ejecutó en Production con el workflow [34644659293](https://github.com/cococelgit/LSCInventoryCopartIaaI/actions/runs/34644659293), usando el job temporal `job-lsc-v2-reader-canary-prod`. El estado de reader se habilitó temporalmente en PostgreSQL, se ejecutaron las rutas V2 y el workflow lo restauró a `false` al finalizar.

| Ruta validada | Resultado | Tiempo |
|---|---:|---:|
| Resumen inicial | 513 lotes, 9 grupos de facets | 1,926 ms |
| Búsqueda/listado | 25 items devueltos | 424 ms |
| Facets core | 513 lotes, 9 grupos | 101 ms |
| VDP por plataforma/lote | Encontrado: `iaai:45624921` | 140 ms |
| Tiempo total | Exitoso | 2,631 ms |

La imagen fue compilada desde el commit `ed7ab28`. No se modificó el job público, no se cambiaron schedules y no se ejecutó ninguna eliminación.

## Sellers enriquecidos

El parity read-only [34645196909](https://github.com/cococelgit/LSCInventoryCopartIaaI/actions/runs/34645196909) confirmó 108 sellers V2-only válidos: 21 de Copart y 87 de IAAI. No hubo sellers V1-only ni conflictos no nulos. El detalle completo está en `inventory-v2-seller-enrichment-108.md`.

## Por qué V1 todavía no se puede retirar

El canary aislado valida que V2 funciona, pero no convierte automáticamente el portal público a V2. El código actual todavía contiene rutas V1 activas cuando el reader global está apagado:

| Dependencia | Ubicación | Consecuencia |
|---|---|---|
| `SearchProjectionAsync` sobre `inventory_search_current` | `PostgresSnapshotStore.cs` | La búsqueda pública sigue dependiendo de V1 si el flag está apagado |
| `RebuildSearchProjectionAsync` desde `auction_lots` y `auction_lot_versions` | `PostgresSnapshotStore.cs` | El warmup/rebuild todavía requiere V1 |
| Fallback de detalle desde `auction_lot_versions` | `PostgresSnapshotStore.cs` | Algunas fichas todavía pueden leer JSONB V1 |
| Auditoría de parity | `PostgresSnapshotStore.InventoryV2Parity.cs` | El reporte todavía compara contra `inventory_search_current` |

Por tanto, **no se ejecutó el DROP de V1 ni el DELETE masivo de JSONB**. Hacerlo ahora podría romper búsqueda, warmup, fallbacks o la propia auditoría de rollback. La secuencia segura que falta es: activar el reader V2 global de forma controlada; verificar una ventana de operación real; eliminar los fallbacks/rebuilds V1 y cambiar parity a una auditoría independiente; ejecutar dry-run de purga; purgar por lotes; y finalmente retirar tablas sólo cuando no queden consumidores.

## Estado final

El reader V2 pasó el canary funcional aislado. La purga destructiva queda bloqueada por dependencias V1 todavía presentes en código, no por un problema de paridad de datos. El JSONB histórico permanece intacto como respaldo reversible.
