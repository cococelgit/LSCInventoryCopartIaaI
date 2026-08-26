# Validación del Inventory Engine

## Corte validado el 25 de agosto de 2026

La revisión `ca-lsc-inventory-api-prod--latestphotos` respondió saludable y leyó lotes persistidos desde PostgreSQL. La consulta de los diez lotes más recientes devolvió diez lotes con fotos reportadas desde `cs.copart.com`.

El bridge tRPC local recibió lotes, fotos y datos de frescura de Azure sin exponer `INVENTORY_API_TOKEN` al navegador. La ficha del lote `41623946` mostró una foto real, puja reportada y la marca de fuente Azure.

El UI ya no utiliza el catálogo local como respaldo. Si Azure no responde, presenta un estado explícito de carga, error o lote no disponible.

La validación posterior confirmó 44 lotes, 66 versiones auditables y fotos reportadas para los 44 lotes persistidos. Los endpoints internos rechazan solicitudes sin el token de servicio y mantienen acceso únicamente para llamadas autorizadas.

La última corrida controlada concluyó correctamente y elevó el corte a 57 lotes con fotos reportadas. Apibara reportó plan `Test` con límite efectivo de 100 solicitudes y 75 restantes en la consulta de seguimiento. El UI se publicó correctamente en `lsc-inv-revi-zyn4tlbw.manus.space` tras reintentar un timeout temporal del registro de imágenes.

El dominio publicado se verificó directamente: el listado mostró 57 lotes del corte, 156 fotos reportadas, datos de frescura de Azure y fichas de inspección accesibles. La ausencia de foto se etiqueta explícitamente cuando el feed no la reporta.

La ficha publicada del lote `57658216` mostró seis fotos reales, puja reportada, ubicación de Clewiston (Florida), fecha de subasta y campos ausentes presentados como no disponibles. La ficha conserva la fuente Azure y no muestra credenciales ni información de acceso.
