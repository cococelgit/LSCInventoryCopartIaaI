# Read Model de búsqueda LSC para 200,000+ vehículos

## Objetivo operativo

El buscador no debe reconstruir el estado actual desde `auction_lot_versions` en cada request. La ruta crítica debe consultar una sola fila actual por lote, con columnas escalares indexables y el payload canónico disponible únicamente para devolver la ficha.

| Métrica | Objetivo |
|---|---:|
| Primera página caliente, 20 vehículos | < 1 segundo en API |
| p95 de búsqueda con filtros comunes | < 2 segundos |
| Facetas iniciales calientes | < 1 segundo |
| Capacidad inicial | 200,000–500,000 lotes activos |
| Consistencia | Eventual, normalmente dentro de la misma transacción de persistencia |

## Componentes

### `inventory_search_current`

Read model con una fila por `lot_key`. Contiene columnas escalares para todos los filtros visibles, el estado activo, los campos de orden y el payload JSON canónico. Se actualiza mediante `UPSERT` cada vez que Copart Excel o IAAI persisten un lote.

La tabla es propiedad del principal de runtime que la crea. No altera ni reemplaza `auction_lots`, `auction_lot_versions` o `inventory_lot_lifecycle`.

### Índices

Los índices parciales se concentran en filas activas y cubren: plataforma/fecha, marca/modelo, año, puja, Buy Now, odómetro, estado, facility, título, daño y campos de condición. Un índice GIN de texto completo cubre lote, VIN, marca, modelo y título.

### `inventory_search_facet_counts`

Guarda conteos precalculados para las facetas globales del Home. Se refresca después de un backfill y al terminar una ejecución completa. Las facetas dependientes —principalmente modelos para marcas seleccionadas— se calculan sobre `inventory_search_current`, no sobre el historial.

### Backfill idempotente

Una operación set-based carga la última versión por lote desde las tablas existentes hacia el read model. Usa `ON CONFLICT DO UPDATE`, puede repetirse y no elimina inventario. El estado se registra con una versión de proyección para evitar reconstrucciones innecesarias.

## Flujo de escritura

1. El adaptador limpia el vehículo.
2. Se persisten `auction_lots`, versión histórica, lifecycle y payload de auditoría.
3. En la misma operación lógica se hace `UPSERT` del read model.
4. Una reconciliación completa actualiza `is_active` en lifecycle y read model.
5. El cierre de la corrida refresca facetas agregadas fuera de la ruta crítica del usuario.

## Flujo de lectura

1. `search` consulta únicamente `inventory_search_current`.
2. El total usa el mismo conjunto filtrado y los índices apropiados.
3. La primera página se solicita en paralelo con el resumen de facetas.
4. El Home renderiza los primeros 20 vehículos tan pronto llega `search`; no espera las facetas.
5. La ficha consulta por `lot_number` indexado en el read model.

## Escalabilidad y límites

PostgreSQL es suficiente para cientos de miles de lotes siempre que la búsqueda trabaje sobre una tabla actual indexada. Si el inventario crece a varios millones o se requiere faceting dinámico complejo con p95 sub-segundo global, la siguiente etapa sería OpenSearch como read model secundario, manteniendo PostgreSQL como fuente de verdad. Para el objetivo actual, añadir OpenSearch sería complejidad y costo prematuros.

## Seguridad

El navegador nunca accede a PostgreSQL ni recibe tokens. Todo pasa por API .NET protegida y bridge tRPC server-side. Copart continúa exclusivamente por Excel e IAAI exclusivamente por Apibara.
