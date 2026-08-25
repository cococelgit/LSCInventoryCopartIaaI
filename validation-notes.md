# Validación del Inventory Engine

## Corte validado el 25 de agosto de 2026

La revisión `ca-lsc-inventory-api-prod--latestphotos` respondió saludable y leyó lotes persistidos desde PostgreSQL. La consulta de los diez lotes más recientes devolvió diez lotes con fotos reportadas desde `cs.copart.com`.

El bridge tRPC local recibió lotes, fotos y datos de frescura de Azure sin exponer `INVENTORY_API_TOKEN` al navegador. La ficha del lote `41623946` mostró una foto real, puja reportada y la marca de fuente Azure.

El UI ya no utiliza el catálogo local como respaldo. Si Azure no responde, presenta un estado explícito de carga, error o lote no disponible.

La validación posterior confirmó 44 lotes, 66 versiones auditables y fotos reportadas para los 44 lotes persistidos. Los endpoints internos rechazan solicitudes sin el token de servicio y mantienen acceso únicamente para llamadas autorizadas.
