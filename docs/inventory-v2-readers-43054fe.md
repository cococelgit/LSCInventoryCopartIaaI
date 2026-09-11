# Inventory V2 Readers — Commit 43054fe

## Alcance

Se implementaron lectores estructurados para `SearchAsync`, `GetInventorySearchSummaryAsync`, `GetByPlatformAndLotAsync` y `GetInventoryFacetsV2Async`. Cuando el flag de configuración `InventoryV2__ReaderEnabled` está apagado, el comportamiento V1 permanece sin cambios. Cuando está encendido, el código exige que PostgreSQL reporte simultáneamente `writer_enabled = true` y `reader_enabled = true`; de lo contrario, conserva el camino V1 o rechaza facets V2 de forma segura.

| Área | Fuente V2 | Estado |
|---|---|---|
| Listado y búsqueda | `inventory_current_v2` + scoring actual | Implementado detrás de doble guard |
| Resumen inicial | facets core V2 | Implementado detrás de doble guard |
| Detalle por plataforma/lote | `inventory_current_v2` + media tipada + scoring | Implementado detrás de doble guard |
| Facets | `inventory_current_v2` con aliases compatibles | Implementado detrás de doble guard |
| Media | `inventory_media_current_v2` en una consulta por página | Implementado |
| JSONB almacenado | No se lee para V2 | Eliminado del read path V2 |
| Schedules y jobs | Sin cambios | Preservados |

## Compatibilidad preservada

El mapper reconstruye `AuctionVehicle` desde columnas tipadas y mantiene los campos que usa el portal: VIN, lote, plataforma, año, marca, modelo, título, daños, condición de marcha, odómetro, seller, location, precios, Buy Now, fechas, scoring y media. El `RawJson` requerido por el contrato interno se genera en memoria a partir del objeto reconstruido; no se lee ningún payload JSONB de PostgreSQL.

Los filtros críticos mantienen la semántica existente: sólo lotes activos, Buy Now requiere precio positivo, `WithPhotosOnly` usa `media_has_photos`, el rango de puja usa `current_bid_usd`, la fecha usa `auction_at`, y scoring continúa entrando por el join de `inventory_vehicle_score_current`.

## Validación ejecutada

| Validación | Resultado |
|---|---:|
| `dotnet build Lsc.Inventory.sln -c Release --no-restore` | Exitoso, 0 errores, 0 warnings |
| `dotnet test Lsc.Inventory.sln -c Release --no-restore` | 265 aprobadas, 0 fallos |
| Diff whitespace check | Exitoso |
| Flag reader global | Permanece apagado; no se habilitó Production |
| Commit remoto | `43054fe` en `main` |

## Canary recomendado

El siguiente paso no es activar el reader global. Primero se debe desplegar el commit en un job o endpoint interno que ejecute, para una muestra limitada de lotes reales, la misma solicitud contra V1 y V2 y compare: total, identidad de lotes, VIN, Buy Now, título, daños, seller, location, fecha de subasta, scoring y cantidad de media. Las diferencias deben clasificarse como `exact`, `enrichment`, `missing` o `expected-normalization`.

Después de un canary exitoso, se puede activar `InventoryV2__ReaderEnabled=true` junto con el estado de base de datos, manteniendo el rollback inmediato mediante el mismo flag y sin modificar schedules. La primera activación debe ser interna o limitada; la activación pública requiere además confirmar latencia P50/P95 y errores de las rutas de búsqueda, resumen, facets y VDP.

## Riesgos pendientes antes del cutover

La reconstrucción tipada cubre el contrato público principal, pero no constituye todavía una prueba de producción. Deben validarse con datos reales los alias de título especial, la clasificación de seller, el orden exacto de media y cualquier campo adicional contenido históricamente en `AdditionalData`. También debe comprobarse la paridad del conteo total y de facets bajo filtros combinados, no sólo la compilación y las pruebas estáticas.
