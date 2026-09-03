- [x] Inspeccionar los payloads auditados para confirmar URLs y campos reales de media.
- [x] Renovar el acceso de lectura a registros de Azure con un código de dispositivo vigente.
- [x] Verificar que la cuenta desbloqueada ya permite consultar los registros de diagnóstico.
- [x] Definir el acceso temporal privado a imágenes y datos completos sin exponer la llave del proveedor.
- [x] Integrar galería real y ficha ampliada por lote en una nueva pestaña.
- [x] Verificar la interfaz, guardar versión y compartir la URL temporal actualizada.

- [x] Elegir la frecuencia de actualización del feed y el alcance de fotos en vivo.
- [x] Resolver la integración full-stack preservando el UI existente y los secretos server-side.
- [x] Conectar datos y media mediante API segura, con estados de carga y error.
- [x] Verificar actualizaciones, permisos, cuota del proveedor y publicar la nueva versión.

- [x] Diseñar el flujo operativo de inventario cacheado con actualización automática cada 30 minutos.
- [x] Medir consumo de cuota de Apibara por ejecución y definir límites por alcance.
- [x] Implementar el proceso automático y la lectura del UI desde el inventario persistido.
- [x] Validar frescura, fallos, duplicados, media y publicación temporal.

- [x] Verificar la cuota activa expuesta por Apibara: plan Test, límite efectivo 100, 75 solicitudes restantes; escalar el plan antes de agotar el límite.
- [x] Conectar el backend del UI al inventario persistido del motor sin exponer secretos.
- [x] Configurar una ejecución automática cada 30 minutos con reintentos controlados y estado de frescura.
- [x] Validar una corrida controlada, fotos, errores, cuota restante y publicación temporal.

- [x] Publicar una API Azure de lectura protegida como única fuente del UI.
- [x] Mantener PostgreSQL, Blob y la llave de Apibara fuera del alcance público.
- [x] Activar el job de sincronización automática cada 30 minutos con auditoría de uso bajo demanda protegida.
- [x] Conectar el UI a la API Azure y verificar frescura, fotos y estados de error.

- [x] Ejecutar la verificación y el despliegue directamente desde la sesión Azure autenticada, sin pedir al usuario copiar comandos.

- [x] Corregir el job programado y validar ejecuciones exitosas posteriores sin alteraciones administrativas de datos.

- [x] Actualizar en Key Vault la clave vigente de Apibara y restaurar de inmediato el acceso privado de la bóveda.
- [x] Repetir una corrida controlada después de validar la nueva credencial antes de considerar estable el cron de 30 minutos.
- [x] Corregir la API de lectura para no exigir Apibara al arrancar cuando la sincronización está desactivada.
- [x] Verificar la revisión Azure corregida, el puente tRPC, las fotos dinámicas y los estados de frescura.
- [x] Guardar un nuevo checkpoint del UI conectado a Azure y registrar la versión publicada: `84fa3973`.
- [x] Activar y validar la revisión de API que lee snapshots persistidos en PostgreSQL.
- [x] Verificar una respuesta de inventario con lotes y fotos reales tras la corrección de lectura.
- [x] Confirmar el bridge tRPC y la ficha de detalle en el UI con datos vivos.
- [x] Publicar y validar la versión final del UI conectado en su URL desplegada.
- [x] Ajustar el consumo del job a la cuota real expuesta por Apibara antes de mantener el cron de 30 minutos.
- [x] Mantener el alcance completo del feed y escalar el plan de Apibara cuando la cuota operativa lo requiera, conforme a la instrucción del usuario.
- [x] Eliminar los fallbacks de catálogo local del UI y mostrar un estado explícito cuando Azure no responda.
- [x] Definir y validar una auditoría de uso bajo demanda protegida que no multiplique llamadas en cada ejecución del cron.
- [x] Proteger los endpoints internos de validación, uso y sincronización con el token de servicio para impedir acceso o ejecuciones públicas.
- [x] Reintentar la publicación del UI tras el timeout de red del registro de imágenes y validar el despliegue resultante.
- [x] Revisar la carpeta oficial de recursos de LSC y seleccionar logo, variantes y colores de marca aprobados.
- [x] Sustituir la identidad visual provisional del Inventory UI por los activos oficiales de LSC.
- [x] Validar y publicar la actualización visual de marca en el dominio del Inventory UI.
- [x] Analizar la referencia de BidCars y definir una jerarquía de búsqueda adaptada a LSC sin copiar su identidad.
- [x] Reemplazar el hero y la consola de evidencia por una barra de búsqueda compacta y filtros completos fijos a la izquierda.
- [x] Rediseñar el listado derecho como resultados densos con foto real, datos clave, puja y enlace de ficha en nueva pestaña.
- [x] Validar filtros, datos vivos y comportamiento responsive del buscador simplificado.
- [x] Publicar y verificar el rediseño del buscador en el dominio de LSC.
- [x] Revisar y normalizar los valores disponibles de daño, título y transmisión desde el corte Azure: el corte actual reporta “No reportado” en los 57 lotes.
- [x] Añadir filtros laterales funcionales de tipo de daño, estado de título y transmisión, con opciones dinámicas basadas en el corte Azure.
- [x] Validar los nuevos filtros con datos vivos y publicar la mejora.
- [x] Inventariar los filtros relevantes de BidCars y contrastarlos con los campos reales de Azure.
- [x] Añadir filtros funcionales de odómetro, tipo de tracción y tipo de combustible.
- [x] Añadir los filtros adicionales soportados por el feed y estados honestos para los no disponibles.
- [x] Validar la barra lateral ampliada, filtros combinados y comportamiento responsive con el corte Azure.
- [x] Publicar y verificar la barra lateral ampliada en el dominio de LSC, incluidos filtros inferiores y una prueba directa de modelo en producción.
- [x] Auditar los campos de facility, estado, odómetro, daño, título, tracción y combustible en el contrato y payload del proveedor: facility/estado ya existen; el título se estaba descartando en la salida pública; los demás campos requieren lectura de detalle cuando no vengan en la lista.
- [x] Enriquecer la sincronización para persistir datos reales por lote sin aumentar llamadas innecesarias.
- [x] Corregir el mapeo público del estado de título y conservar facility/estado en el contrato del UI.
- [x] Agregar filtros activos de ubicación/facility y estado a la barra lateral.
- [x] Ejecutar una sincronización controlada, validar los campos enriquecidos y publicar el buscador actualizado.
- [x] Guardar la versión verificable del UI con filtros de facility/estado y el contrato actualizado.
- [x] Validar el dominio publicado con facility/estado y al menos un campo enriquecido real.
- [x] Formalizar el concepto como presupuesto LSC base —no total out-the-door— usando exclusivamente puja actual y el broker fee confirmado.
- [x] Alinear el filtro y las tarjetas al mismo concepto de presupuesto LSC base con exclusiones explícitas.
- [x] Documentar dentro del proyecto la instrucción operativa proporcionada por LSC para el broker fee usado en el cálculo.
- [x] Probar los nuevos filtros con datos vivos y validar en el dominio publicado la fecha de subasta y el rango monetario final antes de marcarlos completos.
- [x] Probar los nuevos filtros con datos vivos, publicar y validar el dominio actualizado.
- [x] Probar funcionalmente en la interfaz el rango de fecha de subasta con un corte vivo.
- [x] Probar funcionalmente en la interfaz el rango de presupuesto LSC base con un corte vivo.
- [x] Registrar evidencia final de ambos filtros en producción y publicar el cierre de validación.
- [x] Verificar mediante el endpoint protegido el plan, la cuota y los límites efectivos de Apibara tras el upgrade.
- [x] Ajustar el alcance del job en función de la cuota confirmada y lanzar una sola sincronización controlada.
- [x] Validar lotes, frescura y campos persistidos en el buscador: 487 elegibles; 483 con fotos, 474 con odómetro/daño, 487 con título y fecha.
- [x] Adaptar el motor para procesar las 14 facilities Copart devueltas por Florida sin depender de una sola facility configurada.
- [x] Desplegar el motor multi-facility con límites conservadores por corrida y ejecutar una única corrida controlada: 14 scopes, 280 lotes observados, 35 solicitudes y 0 fallos.
- [x] Verificar cobertura por facility, cuota restante y filtros de ubicación/estado: 14 facilities, estado FL, 29,782 solicitudes restantes y 23 pruebas aprobadas, incluidas pruebas vivas de facility y estado.
- [x] Convertir el prompt D00A–D10 en una especificación determinística con precedencia, evidencia y salida auditable.
- [x] Mapear cada campo autorizado del filtro contra los payloads reales de lista y detalle de Apibara.
- [x] Resolver con LSC las ambigüedades y datos obligatorios antes de filtrar o insertar nuevos vehículos.
- [x] Implementar 23 pruebas del motor por regla y bloquear la carga de lotes descartados antes de PostgreSQL.
- [x] Mapear `auction.auction_at`, `facility.state`, `seller.name`, `condition.primary_damage`, `condition.secondary_damage` y `sale_document.name/is_pending` a la entrada autorizada del filtro.
- [x] Aplicar únicamente D00A, D00B, D01–D08 y D10 como descartes; Rebuilt y cualquier otro tipo de título deben cargarse.
- [x] Crear un filtro UI de tipo de título que oculte por defecto Certificate of Destruction, Junk, Non-Repairable y Parts Only sin eliminarlos de la base.
- [x] Permitir mostrar títulos especiales seleccionándolos expresamente en el filtro y conservarlos disponibles por búsqueda de lote.
- [x] Diseñar paginación eficiente para la lista filtrada sin perder filtros, orden ni búsqueda activa.
- [x] Persistir en PostgreSQL las decisiones DESCARTAR con regla, evidencia, versión y fecha, sin almacenar el VIN completo en el registro de auditoría.
- [x] Exponer un endpoint Azure protegido y un bridge server-side para consultar descartes paginados.
- [x] Crear un panel interno protegido con resumen por regla, listado de descartes y evidencia por vehículo.
- [x] Añadir navegación entre buscador y panel interno sin exponer credenciales ni convertir el panel en una ruta pública anónima.
- [x] Probar paginación, control de acceso, datos reales y responsive antes de publicar.
- [x] Renderizar el panel interno con descartes reales del endpoint protegido y verificar resumen, fila y evidencia.
- [x] Ejercitar la paginación con el corte real, incluida navegación, conteos y reinicio al cambiar el orden.
- [x] Publicar un checkpoint y repetir validaciones básicas sobre la versión servida.
- [x] Verificar mediante pruebas DOM y CSS el drawer móvil, la paginación apilada y la tabla interna desplazable.
- [x] Confirmar los endpoints IAAI de Apibara, 270 facilities observadas en 50 estados y 61,793 lotes abiertos únicos con año 1900–2027, sin descargar detalles individuales.
- [x] Calcular páginas, solicitudes, duración y margen de cuota para una carga inicial completa de IAAI: 3,230 páginas, piso paralelo de 3.76 minutos, ventana productiva estimada de 45–90 minutos y 26,091 solicitudes restantes.
- [x] Definir el proceso de backfill completo, reglas previas a PostgreSQL y sincronización incremental de IAAI.
- [x] Entregar la recomendación operativa con alcance, riesgos y secuencia exacta de implementación.
- [x] Documentar el alcance de facilities como ubicaciones observadas en lotes abiertos, no como catálogo administrativo completo de IAAI.
- [x] Pausar inmediatamente el job actual para detener nuevas llamadas Copart a Apibara sin borrar el inventario existente.
- [x] Separar permanentemente las fuentes en código y configuración: IAAI=Apibara; Copart=adaptador Excel entregado por la otra tarea.
- [x] Actualizar manifests y jobs para impedir que cualquier ejecución Apibara incluya la plataforma Copart.
- [x] Dejar preparado el contrato del adaptador Excel, sin duplicar la implementación que se está construyendo en la otra tarea.
- [x] Resolver el conflicto entre el adjunto D09 y la política vigente: ningún tipo de título, incluido Rebuilt, debe descartarse solo por su categoría.
- [x] Añadir D00C, M00, D00D, Q01, Q04 y marcas M01–M08 al motor determinístico compartido.
- [x] Implementar normalización técnica, comercial y evidencia raw/normalized antes de persistir vehículos aceptados.
- [x] Incorporar estados de reconciliación y reglas de reactivación/despublicación sin borrar historial.
- [x] Ejecutar pruebas unitarias por regla, precedencia, títulos permitidos y limpieza canónica: 55 pruebas aprobadas.
- [x] Ejecutar un piloto controlado de 1,000 vehículos IAAI list-only y medir aceptados, descartados, marcados, errores y consumo.
- [x] Validar en PostgreSQL/Blob y en el UI los vehículos elegibles del piloto antes de ampliar a todo IAAI.
- [x] Exponer la plataforma por vehículo en el contrato público y el bridge para distinguir Copart de IAAI.
- [x] Activar IAAI como fuente seleccionable en el UI y eliminar la etiqueta provisional “pronto”.
- [x] Validar fotos IAAI, conteo y paginación con los 1,000 vehículos elegibles del piloto.
- [x] Crear un carrusel reutilizable en cada tarjeta con foto actual, contador y controles anterior/siguiente.
- [x] Implementar swipe horizontal táctil sin activar accidentalmente el enlace de la ficha.
- [x] Mantener lazy loading, fallback de imagen y navegación por teclado accesible.
- [x] Probar varias fotos, una sola foto, swipe, controles, apertura de ficha, paginación y responsive: 36 pruebas aprobadas.
- [x] Publicar y validar el carrusel con fotos reales IAAI en producción.
- [x] Guardar un checkpoint que incluya el carrusel dentro de las tarjetas del listado.
- [x] Verificar en el dominio servido el bundle del carrusel y repetir sus pruebas de interacción contra datos vivos.
- [x] Ejecutar una prueba browser automatizada contra producción que avance y retroceda fotos reales IAAI.
- [x] Simular swipe táctil sobre el carrusel publicado y confirmar que el contador cambia sin abrir la ficha.
- [x] Confirmar en producción que el enlace principal sigue abriendo la ficha del lote en una nueva pestaña.
- [x] Auditar remotos y cambios pendientes de Inventory Engine e Inventory UI antes de consolidarlos.
- [x] Excluir secretos, respuestas de proveedor, temporales, outputs y artefactos locales del commit inicial.
- [x] Consolidar ambos proyectos en la estructura del repositorio seleccionado sin perder historial operativo útil.
- [x] Documentar el contrato y punto de integración para que otra tarea conecte Copart Excel al Inventory Engine.
- [x] Crear y subir un único commit inicial con motor, UI, pruebas, infraestructura y documentación.
- [x] Verificar en GitHub la rama, commit y archivos principales después del push.

## Copart Excel streaming integration

- [x] Implementar `ICopartExcelSnapshotAdapter` para snapshots Copart descargados al servidor, sin llamadas a Apibara.
- [x] Validar extensión, tamaño, SHA-256, columnas obligatorias, estructura y completitud antes de permitir reconciliación.
- [x] Procesar CSV/Excel de Copart en streaming con memoria acotada y conservar fila raw más campos normalizados en `AuctionVehicle`.
- [x] Conectar el adaptador al núcleo existente: `CanonicalVehicleCleaner`, `AuctionEligibilityEvaluator` v4, auditoría, PostgreSQL, Blob y reconciliación.
- [x] Mantener D09 desactivada y preservar títulos especiales para el filtro UI; no introducir descarte por tipo de título.
- [x] Proteger el aislamiento de fuentes: IAAI solo Apibara; Copart solo Excel; bloquear Copart antes de llamadas Apibara.
- [x] Añadir pruebas de archivo válido/grande, hash duplicado, snapshot incompleto, aceptados/descartados, aislamiento Apibara, tres misses, reactivación y métricas auditables.
- [x] Ejecutar `pnpm check`, `pnpm test` y `dotnet test inventory-engine/Lsc.Inventory.sln -c Release`: check y 63 pruebas .NET aprobadas; `pnpm test` conserva 5 fallos previos del corte vivo/UI, no atribuibles al procesador Copart.
- [x] Ejecutar un dry run de 1,000 filas Copart, reportar observados/aceptados/descartados/duplicados/errores/duración/memoria y solicitar aprobación antes de publicar.
- [x] Subir la implementación validada a la rama `main` sin secretos ni artefactos de snapshots.
- [x] Publicar el snapshot Copart completo verificado en producción desde Blob: 145,710 observados, 58,829 aceptados, 86,871 descartados, 10 cuarentena, 58,829 marcados, 0 errores; manifiesto completo y reconciliado.
- [x] Verificar persistencia de producción sin VINs completos: 59,059 lotes Copart totales, 58,836 activos, 223 inactivos y decisiones auditadas por estado.

## Paginación de inventario completo

- [x] Identificar el límite de 1,000 registros entre el bridge del portal y el endpoint Azure.
- [x] Implementar consulta paginada, filtrada y source-aware desde PostgreSQL para Copart e IAAI.
- [ ] Desplegar la API paginada y el bridge/UI actualizado.
- [ ] Validar en producción que el portal recorra todos los resultados elegibles y no solo el primer corte.

## Operación recurrente Copart

- [x] Configurar el descargador Copart en `25,55 * * * *` UTC, cinco minutos antes de la carga.
- [x] Crear un Job programado dedicado `job-lsc-copart-auto-prod` con `0,30 * * * *` UTC y el comando `--copart-excel-run`.
- [x] Conservar `job-lsc-copart-excel-prod` como Job manual para diagnóstico y recuperación, sin convertirlo en la producción recurrente.
- [ ] La tarea IAAI debe configurar su propio Job en `15,45 * * * *` UTC y, antes de elevar frecuencias, ambos flujos deben implementar/validar un candado compartido de escritura.
- [x] Confirmar la primera ejecución disparada por cron en `job-lsc-copart-auto-prod`: inició a las 14:20 UTC, terminó a las 14:43:43 UTC y finalizó `Succeeded` sin intervención manual.

## Integración Copart con Centro de Ejecuciones

- [x] Confirmar que la tabla de sesiones existente es `inventory_sync_runs`; no recrear ni duplicar el Centro de Ejecuciones.
- [x] Registrar cada corrida Copart como `provider=copart-excel`, `platform=copart` y completar sus resultados en `inventory_sync_runs`.
- [x] Registrar archivos inválidos y SHA duplicados como ejecuciones visibles, con datos seguros y sin duplicar cargas.
- [x] Validar en producción que la corrida automática Copart aparezca en el Centro de Ejecuciones: ejecución 16:00 UTC completó correctamente con 145,583 observados; siguiente ejecución 16:30 UTC fue creada por cron.

## Media Copart: fotos completas y HD

- [x] Auditar columnas, URLs y cobertura de media del snapshot Copart sin exponer VINs ni parámetros sensibles.
- [x] Verificar desde la ruta Azure autorizada si `Image URL` resuelve el catálogo completo de fotos y variantes HD: 11/12 catálogos respondieron; promedio 12.09 fotos y 12.09 enlaces HD.
- [x] Definir y probar el mapeo de todas las imágenes por lote, con fallback seguro para miniatura y URLs rotas.
- [x] Persistir y publicar galerías Copart completas sin consultar Apibara ni alterar la elegibilidad.
- [x] Medir la primera sincronización enriquecida: 1,000 candidatos, 993 galerías HD resueltas, 7 fallos seguros, 57.76 s de proceso.
- [x] Activar `job-lsc-copart-media-prod` en `25,55 * * * *` UTC: procesa 5,000 lotes por turno, con concurrencia 8, sin elegibilidad ni reconciliación.

## Cobertura de campos Copart

- [x] Medir el CSV Copart y confirmar que VIN, vendedor y campos técnicos se distinguen entre datos fuente y campos no expuestos.
- [x] Exponer VIN enmascarado y vendedor condicionalmente, sin filtrar el VIN completo al portal.
- [x] Promover los campos Copart de alto valor ya disponibles: trim, carrocería, motor, cilindros, valor estimado, costo de reparación, estado del título y condición del lote.
- [x] Actualizar bridge, lista y ficha del repositorio para mostrar datos reales y eliminar textos fijos engañosos.
- [x] Desplegar y validar la API de Azure con fallback seguro al payload raw ya auditado; el portal publicado actual permanece en un proyecto/bundle separado y debe desplegar estos cambios desde `main`.
- [x] Validar sin afectar elegibilidad, Copart media ni IAAI: 64/64 pruebas .NET y `pnpm check` aprobados; `pnpm test` conserva 3 fallos en 5 archivos por expectativas antiguas de la UI y pruebas de feed vivo.

## Mapeo de títulos Copart desde PDF

- [x] Extraer el catálogo del PDF adjunto: 181 códigos con descripciones en inglés y español, más la recomendación fuente conservada como metadato.
- [x] Comparar contra el snapshot: la referencia cubre 145,806 de 146,235 filas con código (99.71%); los códigos no presentes conservarán su código y estado `unmapped` sin descripción inventada.
- [x] Aplicar el catálogo al adaptador Copart y guardar código, descripción, versión y estado de mapeo de forma uniforme.
- [x] Exponer la descripción correcta de título al inventario, conservando el código para auditoría y filtros.
- [x] Desplegar el catálogo en API y Jobs Copart; el mapeo cubre 99.71% de las filas codificadas de referencia. D09 permanece desactivada y la recomendación `Procesar` del PDF es solo metadato, no una regla de elegibilidad.

## Backfill de títulos Copart existentes

- [x] Confirmar que la API puede mostrar títulos existentes mediante fallback, pero que se requiere persistencia para no depender de esa inferencia.
- [x] Crear una operación Copart-only que actualice título, documento de venta y metadatos de mapeo sin re-elegibilidad, reconciliación, media ni lifecycle.
- [x] Ejecutar el backfill de todos los lotes Copart existentes con control de concurrencia, versiones y sesión en el Centro de Ejecuciones.
- [x] Verificar la persistencia en producción: 73,199 títulos mapeados, 453 no mapeados y 0 pendientes de backfill. Los códigos sin referencia conservan su código crudo y estado `unmapped` para revisión.

## Historial de intentos y señal de oportunidad Copart

- [x] Definir observaciones inmutables por snapshot completo, intentos de subasta derivados y resultados con niveles explícitos de evidencia; contrato en `inventory-engine/notes/copart_auction_attempt_history_v1.md`.
- [x] Registrar una observación Copart por lote y snapshot sin alterar elegibilidad, inventario, lifecycle ni IAAI.
- [x] Derivar intentos por fecha de subasta y marcar `relisted_inferred` solo ante reaparición posterior verificable; no inferir no-venta por desaparición.
- [x] Calcular un score explicable de oportunidad con evidencia, sin afirmar que un vendedor está obligado a vender; sin re-listado inferido el score se mantiene en 0.
- [x] Añadir pruebas de relistado, venta confirmada, ausencia desconocida, cambios de puja y aislamiento de fuentes; `dotnet test inventory-engine/Lsc.Inventory.sln -c Release` aprobó 73/73.
- [x] Ejecutar y verificar dos bloques manuales de backfill desde versiones Copart preservadas: 200,000 observaciones, 64,823 intentos, 1,525 re-listados inferidos y 0 conversiones fallidas. La cobertura histórica completa no se declara terminada; los bloques futuros se procesan con el mismo modo idempotente.
- [x] Crear el handoff para UI/API en `notes/HANDOFF_PORTAL_AGENT_COPART_AUCTION_HISTORY.md`, sin añadir endpoint público ni cambiar IAAI, fuentes, API o jobs automáticos.
- [x] Verificar después de cada ejecución que la imagen de `ca-lsc-inventory-api-prod` permaneció en `acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:inventory-api-integrated-r20-active-summary`.

## Run & Drive desde Excel Copart

- [x] Mapear exclusivamente la columna `Runs/Drives` a texto original y normalización operativa conservadora, sin usar `Drive`/`DriveType`.
- [x] Propagar `run_condition_raw` y `run_condition` por `VehicleCondition.RunCondition`, snapshot/payload, persistencia JSON y contrato público compartido.
- [x] Mantener IAAI/Apibara sin cambios funcionales y no inferir condición desde llaves, daños, título o scoring.
- [x] Cubrir columna ausente, valores explícitos, casing, valor desconocido y preservación raw mediante pruebas deterministas.
- [x] Validar localmente y entregar los nombres finales y ejemplo JSON sanitizado, sin despliegue ni ejecución de jobs. `dotnet test inventory-engine/Lsc.Inventory.sln -c Release` aprobó 87/87 y `pnpm check` aprobó.

## Taxonomía normalizada de títulos Copart

- [x] Añadir catálogo explícito y versionado de categorías, banderas y estado de revisión por código Copart.
- [x] Persistir los campos derivados solo en payload/metadatos Copart, conservando código, descripciones y recomendación fuente.
- [x] Mantener D09 desactivada y no crear descartes automáticos a partir de categorías normalizadas.
- [x] Cubrir títulos clean, branded, salvage, rebuilt, no reparable, export, documento, variante estatal, desconocido e IAAI sin modificación.
- [x] Redactar handoff técnico para que el API exponga los campos sin recalcular taxonomía. Validación local: `dotnet test inventory-engine/Lsc.Inventory.sln -c Release` aprobó 105/105 y `pnpm check` aprobó; no se ejecutó job ni despliegue.

## Grading inline Copart

- [x] Integrar el baseline canónico `lsc_pre_grade_v1` del commit aprobado `5428b3f`, sin copiar ni modificar la fórmula.
- [x] Persistir cada lote Copart elegible junto con su resultado canónico de grading en una transacción PostgreSQL antes de que la proyección quede visible.
- [x] Conservar idempotencia por `policy_version` e `input_hash`; un score vigente conserva `scored_at` y se marca como `scoreSkippedUnchanged`.
- [x] Mantener descartes/cuarentenas fuera de grading inline y conservar `MARCAR` con su resultado `MANUAL_REVIEW`.
- [x] Registrar en el manifiesto Copart `created`, `updated`, `unchanged`, `scoredInline`, `scoreSkippedUnchanged`, `scoreFailed`, duración acumulada y p50/p95; las corridas históricas permanecen como `N/D`.
- [x] Cubrir Copart elegible, marcado, payload idéntico, cambio relevante y fallo atómico mediante pruebas. `dotnet test inventory-engine/Lsc.Inventory.sln -c Release` aprobó 118/118 y `pnpm check` aprobó. No se ejecutó ningún Job ni deployment.

- [x] Desplegar la imagen de grading inline exclusivamente al job `job-lsc-copart-excel-prod` y verificar el control idempotente: el snapshot SHA `41e7b6bfd862…` ya estaba completado, por lo que no se reprocesó ni duplicó información.
- [x] Ejecutar `scoring_backfill` exclusivo de Copart: 0 candidatos, 0 scores nuevos, 0 fallos y 0 pendientes; el reporte posterior confirmó cobertura completa de los 63,926 Copart activos con política `lsc_pre_grade_v1`.
- [x] Verificar antes/después de las ejecuciones que `ca-lsc-inventory-api-prod` permanece en `acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:inventory-api-integrated-r66-title-taxonomy-readonly`.

## PR Copart: taxonomía canónica y media HD controlada

- [x] Reemplazar la taxonomía Copart paralela por `TitleFacetCategory` canónico, preservando el título/código fuente y clasificando solo después de elegibilidad.
- [x] Persistir `source_title_raw`, categoría, flags, estado de revisión y versión únicamente para lotes Copart aceptados; IAAI permanece sin cambios y D09 sigue desactivada.
- [x] Mantener el backfill de títulos en la misma autoridad canónica, sin ejecutar backfill dentro de este PR.
- [x] Fortalecer el enriquecimiento de media separado: galería por secuencia, preferencia HD, preservación de referencias originales, 404/URL inválida/transitorio controlados y métricas runtime.
- [x] Cubrir taxonomía, idempotencia, IAAI, HD, secuencia, galería completa, 404, URL inválida, transitorio e independencia de media frente a elegibilidad/scoring.
- [ ] Abrir PR de código y pruebas sin desplegar, ejecutar jobs, cambiar cron, API, IAAI, Apibara, secretos, identidad ni infraestructura.

## Buy Now Copart: precio estrictamente positivo

- [x] Normalizar `Buy-It-Now Price` exclusivamente en el adaptador Copart: solo un decimal mayor que cero se persiste como `buy_now_usd`; cero, negativo, vacío o inválido se convierten a `null`.
- [x] Mantener `CurrentBidUsd = 0` válido y separado de Buy Now, sin cambiar IAAI ni el limpiador genérico.
- [x] Cubrir Buy Now positivo, cero, negativo, vacío, inválido y puja cero con pruebas deterministas. `dotnet test inventory-engine/Lsc.Inventory.sln -c Release` aprobó 129/129 y `pnpm check` aprobó.
- [ ] El agente de API/portal debe filtrar, contar y mostrar Buy Now mediante `buy_now_usd > 0` antes de paginar; este cambio no despliega API ni jobs.

## Copart Pre-Grade v2 con banderas

- [x] Mantener IAAI en `lsc_pre_grade_v1` y aplicar `lsc_pre_grade_v2` exclusivamente a Copart.
- [x] Convertir incertidumbres Copart no bloqueantes (`M02`, `M04`, `M07` y demás `MARCAR`) en `PRE_GRADED_WITH_FLAGS` con pre-grado numérico, confianza, penalidades y códigos explicables.
- [x] Conservar `DISCARDED` y cuarentenas sin nota numérica; no modificar D01–D10, D09, fuentes, media, Buy Now ni reconciliación.
- [x] Corregir el reconocimiento de `RUNS_AND_DRIVES` normalizado como condición mecánica afirmativa sin usar `DriveType`.
- [x] Hacer que persistencia, backfill y cobertura exclusivos de Copart detecten el cambio de política v1 → v2; el estado compartido calcula versión esperada por plataforma para no reencolar Copart v2 como pendiente.
- [x] Validar con 134/134 pruebas .NET y `pnpm check`; simulación del snapshot de referencia a fecha de venta válida: 61,109 lotes elegibles pasarían a `PRE_GRADED_WITH_FLAGS`, frente a no recibir pre-grado v1 por M04/M07. No se ejecutó job ni despliegue.
- [x] Preparar `PROMPT_API_AGENT_COPART_PRE_GRADE_V2.md` para notificar compatibilidad de contrato y presentación al agente de API antes de promover v2.

## Métricas de persistencia por ejecución Copart
- [x] Identificar si cada persistencia de lote Copart fue alta nueva, actualización o sin cambio, sin alterar elegibilidad, historial ni lifecycle.
- [x] Confirmar que los conteos de altas, actualizaciones y sin cambio se persisten en el manifiesto de cada snapshot Copart completo; exponerlos en auditoría mediante unión por `run_id`, sin migración general.
- [x] Mantener `N/D` para duplicados, lock ocupado, snapshots inválidos e invocaciones sin procesamiento por filas.
- [x] Añadir pruebas de conteo por resultado de persistencia y de no-op seguro.
- [x] Ejecutar validación .NET y TypeScript, revisar el diff y publicar únicamente los cambios Copart autorizados. La promoción queda bloqueada hasta que el API interno implemente la unión de auditoría.

## Watermark temporal Copart por `Last Updated Time`

- [x] Mapear `Last Updated Time` como timestamp UTC de origen sin convertirlo en columna obligatoria del CSV.
- [x] Vincular el watermark por lote a la versión vigente de elegibilidad, taxonomía y scoring para invalidarlo cuando cambien reglas.
- [x] Procesar completamente lotes nuevos, timestamps posteriores, fingerprints diferentes y timestamps ausentes/inválidos.
- [x] Mantener todos los lotes elegibles presentes dentro de la reconciliación, incluidos los omitidos por watermark.
- [x] Evitar falsos `unchanged` exigiendo un watermark previo del lote y coincidencia de fingerprint.
- [x] Persistir watermark y métricas incrementales auditables en el manifiesto Copart.
- [x] Añadir pruebas de primera corrida, lote nuevo con timestamp viejo, actualización, mismo timestamp con cambio, timestamp inválido y reconciliación segura.
- [x] Ejecutar pruebas .NET y simulación de desarrollo con el CSV real: 145/145 pruebas; 146,248 observados, 2,031 candidatos, 144,217 omitidos, 5 fallback, 0 errores, 21.612 s; sin deployment.

## Acceso PostgreSQL público restringido a una IP

- [ ] Capturar baseline de red, DNS, jobs activos, API y backups antes de migrar.
- [ ] Abortar automáticamente si IAAI/Copart están ejecutándose o si el servidor no está `Ready`.
- [ ] Migrar PostgreSQL desde VNet Integration al modelo compatible con Private Endpoint.
- [ ] Crear Private Endpoint en `snet-private-endpoints` y zona `privatelink.postgres.database.azure.com` enlazada a las VNet de datos y Container Apps.
- [ ] Verificar conectividad privada de API y jobs usando el mismo FQDN antes de habilitar acceso público.
- [ ] Habilitar acceso público y crear una única regla `/32` para `48.221.10.88`, sin `Allow Azure services`.
- [ ] Validar desde la laptop con `osniel_readonly`, SSL obligatorio y escritura bloqueada.
- [ ] Documentar configuración final, evidencia, riesgos y procedimiento para revocar la regla pública.
