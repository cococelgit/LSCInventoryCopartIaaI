# Plan de carga completa de IAAI

**Fecha de medición:** 25 de agosto de 2026  
**Autor:** Manus AI para La Subasta Cubana

## Resumen ejecutivo

Apibara sí permite traer el inventario IAAI mediante `GET /vehicles?platform=iaai`, usando cursores y un máximo de 20 registros por página.[1] La medición directa realizada con la llave de LSC encontró **61,793 lotes IAAI abiertos únicos** con año de modelo entre 1900 y 2027. Se observaron 62,300 apariciones brutas y 507 repeticiones durante el recorrido de un inventario que seguía actualizándose.

| Métrica medida | Resultado |
|---|---:|
| Lotes IAAI abiertos únicos | 61,793 |
| Apariciones brutas | 62,300 |
| Repeticiones deduplicadas | 507 |
| Facilities distintas observadas en los lotes | 270 |
| Estados de EE. UU. observados | 50 |
| Solicitudes de páginas utilizadas | 3,230 |
| Mínimo teórico a 20 por página | 3,090 |
| Cuota Basic total | 30,000 |
| Cuota restante después de medir | 26,091 |

El total es una fotografía operativa, no una cifra permanente. Apibara indica que los registros generales pueden refrescarse aproximadamente cada 30 minutos y que los lotes cambian al actualizarse, reprogramarse, venderse o retirarse.[1]

Las 270 facilities son **facilities observadas dentro del inventario abierto**, no una afirmación de que constituyan todo el catálogo administrativo de branches de IAAI. El endpoint de locations no entregó una enumeración nacional completa durante esta revisión; por ello, el alcance operativo se respalda con las locations realmente presentes en los 61,793 lotes.

## Decisión recomendada

La carga completa debe ejecutarse como un **job de backfill independiente** del cron de Copart. No se debe añadir IAAI directamente al job actual de 30 minutos, porque una falla o retraso en el recorrido de más de 61,000 lotes podría bloquear la actualización de Copart. Tampoco se deben pedir detalles individuales para todos los vehículos: eso requeriría más de 61,000 solicitudes adicionales y excedería el plan Basic. La lista IAAI ya entrega normalmente fotos, título, daño, vendedor, odómetro y especificaciones normalizadas, por lo que el primer backfill será list-only y el enriquecimiento posterior será selectivo.[1]

## Flujo de carga inicial

| Etapa | Acción | Control |
|---|---|---|
| 1. Preparación | Crear `job-lsc-inventory-iaai-backfill-prod`, separado del job Copart. | Sin cron; ejecución manual única. |
| 2. Particiones | Recorrer años 1900–2027 con cursores independientes. | 8–12 particiones concurrentes; máximo 20 registros por petición. |
| 3. Checkpoints | Guardar año, cursor, páginas, solicitudes y último éxito. | Reanudar sin empezar desde cero si falla. |
| 4. Normalización | Mapear payload IAAI al contrato canónico existente. | Fuente `iaai`; clave idempotente `iaai:{lot_number}`. |
| 5. Elegibilidad | Ejecutar `filtro_elegibilidad_subasta_v3` antes de PostgreSQL. | Aceptados al inventario; rechazados al índice privado con VIN enmascarado. |
| 6. Persistencia | Upsert en PostgreSQL y payload auditable en Blob. | No duplicar el lote ni la misma versión del payload. |
| 7. Cierre | Comparar observados, únicos, aceptados, descartados y errores. | No publicar si el recorrido no terminó completamente. |
| 8. Activación | Habilitar IAAI en el buscador después de validación. | Liberación controlada, no automática. |

## Cambios necesarios antes de mostrar 61,793 lotes

La interfaz actual pagina 24 vehículos, pero el bridge todavía descarga un corte limitado y filtra en el navegador. Eso funciona para cientos de lotes, no para más de 60,000. Antes de activar IAAI se debe crear búsqueda y paginación **server-side en PostgreSQL**, aplicando fuente, facility, estado, año, fecha, título, daño, puja, presupuesto, orden y texto antes de responder al navegador.

La tabla `auction_lots` también necesita campos de control de catálogo: `last_seen_at`, `last_seen_run_id` e `is_active`. Solamente después de terminar un snapshot completo se marcarán como inactivos los lotes IAAI no vistos. No deben borrarse, porque sus versiones y decisiones siguen siendo evidencia operativa.

## Presupuesto de solicitudes

Una carga list-only requiere aproximadamente **3,230 solicitudes** según la medición real, equivalentes a 10.76% de la cuota total Basic. Con 26,091 solicitudes restantes, hay margen para un backfill completo. No existe margen para consultar el detalle de los 61,793 lotes uno por uno.

El job Copart configurado con hasta 35 solicitudes por corrida y 48 corridas diarias tendría un techo teórico de 50,400 solicitudes en 30 días. El consumo real puede ser menor por preservación de enriquecimientos, pero el plan Basic de 30,000 no es suficiente para operar indefinidamente Copart cada 30 minutos, IAAI incremental y reconciliaciones completas. Debemos tratar Basic como plan de arranque y revisar un upgrade antes de programar reconciliaciones nacionales frecuentes.

## Duración

El endpoint de uso reportó una latencia media de 1.118 segundos. Para 3,230 páginas, la duración serial puramente HTTP sería aproximadamente 3,611 segundos —60.2 minutos—. Con 16 particiones concurrentes, el piso matemático es 225.69 segundos —3.76 minutos—, sin incluir reintentos, espera, normalización, reglas, PostgreSQL ni Blob Storage. Para el backfill productivo completo se debe reservar una ventana operativa de **45–90 minutos** y medir la duración real del primer ensayo de 1,000 lotes antes de ejecutar el resto. Esta ventana es una estimación de ingeniería, no una garantía del proveedor.

## Sincronización después del backfill

El backfill inicial se ejecuta una vez. Después, IAAI debe usar un job independiente que recorra únicamente ventanas recientes y próximas de subasta, con checkpoint por partición. Una reconciliación completa se ejecutaría semanalmente al principio, no cada 30 minutos. El proveedor documenta paginación por cursor y recomienda checkpoints para sincronización incremental.[1][2]

| Frecuencia | Alcance recomendado |
|---|---|
| Cada 30 minutos | Páginas más recientes y ventanas próximas de subasta. |
| Diario | Revisión de lotes activos vistos recientemente y cambios de estado. |
| Semanal | Reconciliación completa IAAI, condicionada a cuota suficiente. |

## Riesgos identificados

El filtro documentado `loc_state` devolvió un error SQL 500 del proveedor durante la medición; por tanto, la primera versión no debe depender de ese parámetro para particionar. El conteo se realizó por año y deduplicó por plataforma y lote. También pueden existir registros sin año que no estén incluidos en la cifra de 61,793; deben identificarse mediante una pasada final nacional o soporte del proveedor antes de declarar un total contractual exacto.

## Secuencia de implementación

Primero se implementará la paginación SQL server-side y el control `is_active`. Segundo, se creará el job de backfill reanudable con modo seco y presupuesto de solicitudes. Tercero, se ejecutará una muestra de 1,000 lotes IAAI y se validarán fotos, campos y descartes. Cuarto, se correrá el backfill completo list-only. Quinto, se compararán conteos y se activará la fuente IAAI en el UI. Finalmente, se añadirá el cron incremental independiente con alertas de cuota y fallos.

## Referencias

[1]: https://apibara.tech/en/products/vehicle-auction-data-api/docs "Apibara Vehicle Auction Data API Documentation"
[2]: https://github.com/apibara-tech/apibara-vehicle-auction-api-examples/blob/main/AI_AGENT.md "Apibara Vehicle Auction API — AI Agent Integration Guide"
