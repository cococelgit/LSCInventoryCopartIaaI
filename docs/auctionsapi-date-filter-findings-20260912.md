# AuctionsAPI — hallazgos sobre filtro por fecha de subasta

Fuente oficial: https://auctionsapi.com/auction-docs

Fecha de consulta: 2026-09-12.

## Confirmaciones

- El endpoint de inventario activo es `GET /api/cars`.
- La guía oficial indica que la carga inicial debe pedir `/api/cars` sin el parámetro `minutes`, paginar todas las páginas y guardar los vehículos activos.
- Las actualizaciones usan `/api/cars?minutes=60`.
- Los vendidos/archivados usan `/api/archived-lots?minutes=60`.
- En modo pagado, la documentación indica que `/api/cars` puede devolver hasta 1,000 registros por request en la versión unlimited.
- La documentación incluye `sale_date_in_days`, descrito como lotes cuya fecha de venta es posterior a la fecha de hace X días.
- La documentación incluye `sale_date_from`, descrito como lotes cuya fecha de venta es posterior al valor enviado; tiene prioridad sobre `sale_date_in_days`.
- La documentación incluye `sale_date_to`, descrito como lotes cuya fecha de venta es anterior al valor enviado.
- La documentación incluye el filtro `exclude_expired_auctions=1`, descrito como devolver lotes sin fecha de venta o con fecha futura.
- La documentación incluye `without_sale_date=1`, descrito como devolver únicamente carros sin fecha de subasta.
- La documentación menciona `next_hours_auction`, descrito como lotes cuya subasta ocurre dentro de las próximas X horas.
- La documentación muestra `per_page` y filtros de precios, VIN/lote, plataforma y nomencladores.

## Implicación para el loader

El filtro correcto a probar es `exclude_expired_auctions=1`. No debe confundirse con `without_sale_date=1`, que selecciona precisamente los lotes sin fecha.

La documentación sí muestra `sale_date_from` y `sale_date_to`. Para incluir lotes desde ayer en adelante, la consulta correcta debe enviar `sale_date_from` con el inicio del día anterior en la zona horaria operativa y no debe usar `without_sale_date=1`. Como defensa adicional, el mapper debe validar localmente que `sale_date` no sea null y que no sea anterior al cutoff.

El cliente actual serializa `domain_id`, `minutes`, `page` y `per_page`, pero no serializa todavía `sale_date_from`, `sale_date_to`, `exclude_expired_auctions` ni `next_hours_auction`. Esa es la brecha concreta que debe corregirse antes del siguiente dry-run.

## No ejecutar todavía

No se debe activar escritura ni repetir el bloque de 1,000 hasta implementar el parámetro documentado, confirmar su serialización y ejecutar una prueba read-only comparativa.
