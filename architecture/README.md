# Arquitectura de Copart e Inventario — La Subasta Cubana

Este documento acompaña los diagramas de arquitectura. La finalidad del diseño es procesar el listado de Copart de forma **segura, auditada e incremental**, sin depender de Apibara y sin mezclar su flujo con IAAI.

> **Principio de fuentes:** Copart solo entra por el snapshot CSV/CSV.GZ descargado en Azure. IAAI solo entra por Apibara. Ambos reutilizan el núcleo común de normalización, elegibilidad, auditoría, persistencia y ciclo de vida.

## Diagrama principal

Abra `copart_end_to_end_flow.svg` para una versión escalable y editable del flujo. El archivo PNG tiene la misma arquitectura en alta resolución para compartir por chat o incorporar a documentos. `copart_inventory_architecture.d2` contiene una vista horizontal adicional enfocada en componentes y dependencias; los dos archivos `.d2` son la fuente editable.

## Recorrido completo de un archivo Copart

| Paso | Componente | Qué hace | Control de riesgo |
|---:|---|---|---|
| 1 | Copart | Provee el listado de subasta protegido. | El sistema no asume que un registro de subasta es apto para publicar. |
| 2 | Descargador Azure | Descarga el archivo cada 20 minutos mediante la URL protegida. | Las credenciales y el endpoint viven en Key Vault, no en Git ni logs. |
| 3 | Blob `copart-raw` | Preserva `latest/salesdata.csv.gz` y snapshots fechados. | Permite reproducibilidad y evita depender de un archivo local. |
| 4 | `CopartBlobSnapshotSource` | Busca el snapshot más reciente, descarga por streaming, descomprime gzip y calcula el SHA-256 lógico. | No carga un archivo grande completo en RAM. |
| 5 | Validación del snapshot | Verifica extensión, rango de tamaño, hash, headers, piso de filas y baseline histórico. | Un archivo parcial, corrupto o duplicado no llega a modificar inventario. |
| 6 | Manifiesto Copart | Registra la ejecución en `copart_snapshot_manifests`. | El SHA exitoso es idempotente; un proceso interrumpido solo se recupera en modo explícito. |
| 7 | Adaptador CSV | Lee fila por fila, conserva el JSON original y crea `AuctionVehicle`. | Una fila malformada se manda a cuarentena sin detener el archivo completo. |
| 8 | Limpieza canónica | Normaliza lote, VIN, marca, modelo y demás campos antes de evaluar. | Se retienen los datos raw para auditoría e histórico. |
| 9 | Evaluador v4 | Decide **CARGAR**, **MARCAR**, **DESCARTAR** o **CUARENTENA** de forma determinista. | No se usa un LLM para tomar decisiones de elegibilidad. |
| 10 | Procesador por bloques | Trabaja bloques de 1,000 filas y hasta 12 persistencias controladas. | Acelera sin abrir una concurrencia no limitada contra PostgreSQL/Blob. |
| 11 | Persistencia | Actualiza lote actual, versiones, payload privado y auditoría. | No expone VIN completo, payload raw ni secretos al portal. |
| 12 | Reconciliación | Actualiza presencia y actividad del lote después de una corrida completa sin errores. | Tres snapshots completos ausentes desactivan; reaparecer reactiva; un archivo malo nunca despublica. |
| 13 | API y portal | La API expone solamente lotes activos; el portal los consulta por fuente, lote y página. | El token de lectura queda en el bridge servidor; títulos especiales quedan ocultos por defecto en UI. |

## Política de elegibilidad aplicada

La decisión de carga corresponde a `AuctionEligibilityEvaluator` y usa la política v4. Son descartes automáticos el VIN inválido, la fecha de venta pasada y las reglas D01–D08 y D10. Los flags M01–M08 no bloquean la publicación; alertan al asesor. **D09 está deliberadamente desactivada:** un título rebuilt, junk, certificate of destruction u otro título especial no se descarta solo por su categoría; queda almacenado y el portal lo oculta de manera predeterminada.

| Resultado | Persistencia | Visibilidad |
|---|---|---|
| **CARGAR** | `auction_lots`, `auction_lot_versions` y payload privado. | Portal, mientras el lote esté activo. |
| **MARCAR** | Igual que CARGAR, más flags y evidencia de la decisión. | Portal y asesor, con señal de revisión. |
| **DESCARTAR** | `eligibility_decisions` con regla, versión y evidencia mascarada. | Auditoría interna solamente. |
| **CUARENTENA** | Auditoría de calidad de datos y fallo de fila. | Auditoría interna solamente. |

## Historial y ciclo de vida

El identificador operativo de un listing es `platform:lot`. Por ejemplo, un lote Copart y un lote IAAI no se fusionan aunque compartan VIN. La tabla `auction_lots` guarda la versión vigente; `auction_lot_versions` conserva los cambios de payload, fecha de subasta, puja y estado. La tabla `inventory_lot_lifecycle` controla la publicación: la ausencia en tres snapshots Copart completos y válidos desactiva un lote, y la reaparición lo reactiva.

## Separación entre Copart e IAAI

| Fuente | Entrada autorizada | Núcleo compartido | Regla de aislamiento |
|---|---|---|---|
| Copart | CSV/CSV.GZ en Azure Blob, descargado por el Job de Copart. | Limpiador, política, auditoría, PostgreSQL, Blob y lifecycle. | Cualquier intento Copart por Apibara se bloquea antes de HTTP. |
| IAAI | Apibara exclusivamente. | El mismo núcleo de procesamiento. | No se reemplaza ni modifica por el adaptador Copart. |

## Seguridad y operación

La automatización de entrega usa GitHub Actions con Azure OIDC para construir imágenes inmutables en ACR. El Job Copart es aislado del Job IAAI. La identidad administrada de runtime recibe acceso acotado a Blob, ACR y PostgreSQL mediante Azure AD; no se guardan connection strings ni credenciales de Copart en el repositorio.

La corrida inicial validada del snapshot completo procesó **145,710** filas, aceptó **58,829**, descartó **86,871**, dejó **10** en cuarentena y terminó con **0 errores**. Estos conteos quedan registrados por manifiesto y pueden verificarse sin revelar información sensible.

## Archivos de referencia en el repositorio

| Documento o código | Función |
|---|---|
| `COPART_EXCEL_HANDOFF.md` | Contrato, reglas obligatorias y definition of done de Copart. |
| `inventory-engine/notes/eligibility_policy_v4.md` | Política de elegibilidad autoritativa. |
| `Sources/CopartBlobSnapshotSource.cs` | Lectura de Blob y manejo streaming de CSV.GZ. |
| `Sources/CopartExcelSnapshotAdapter.cs` | Validación y mapeo streaming de las filas Copart. |
| `Workers/CopartExcelSnapshotProcessor.cs` | Batches, concurrencia, auditoría, persistencia y reconciliación. |
| `Storage/PostgresSnapshotStore.cs` | Manifiestos, lotes, versiones, auditoría y lifecycle. |
| `.github/workflows/copart-azure-deploy-and-run.yml` | Construcción OIDC, Job aislado y modos de diagnóstico/recuperación. |
