# Inventory V2 — Fase 1: aislamiento de V1

La Fase 1 introduce una política explícita para el fallback legacy. `InventoryV2Options.LegacyReadFallbackEnabled` conserva la reversibilidad durante la migración, pero evita que V1 sea una dependencia implícita.

Cuando `ReaderEnabled=true` y la base de datos confirma `reader_enabled=true`, búsqueda, resumen, facets y VDP usan V2. Cuando el reader V2 no está disponible, las rutas sólo pueden volver a V1 si `LegacyReadFallbackEnabled=true`. Con el flag en `false`, el engine falla cerrado con un error claro en vez de leer silenciosamente `inventory_search_current`, `auction_lots` o `auction_lot_versions`.

El warmup de la proyección V1 también se detiene cuando el fallback está apagado. El método de rebuild manual queda protegido por el mismo guard. No se modificaron tablas, no se borraron filas y no se purgó JSONB.

La configuración de rollback durante la transición es:

| Configuración | Comportamiento |
|---|---|
| `ReaderEnabled=false`, `LegacyReadFallbackEnabled=true` | Operación legacy V1; rollback disponible |
| `ReaderEnabled=true`, `LegacyReadFallbackEnabled=true` | V2 como primario, V1 como fallback de emergencia |
| `ReaderEnabled=true`, `LegacyReadFallbackEnabled=false` | V2 obligatorio; V1 queda aislado |

El siguiente paso es desplegar el modo V2 primario con fallback todavía habilitado, observar Production durante 24–48 horas y después cambiar el fallback a `false`. Sólo después de esa validación se puede ejecutar el dry-run y la purga por lotes.
