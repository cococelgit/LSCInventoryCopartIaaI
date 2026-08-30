# Estado de despliegue — 2026-08-26

## Código preparado

El monorepo contiene un Engine integrado que combina:

- Ingesta IAAI mediante Apibara, con mezcla lista/detalle y enriquecimiento controlado.
- Adaptador Copart Excel en streaming, separado de Apibara.
- Políticas determinísticas de limpieza, elegibilidad, auditoría y reconciliación.
- Contrato público ampliado para condición, documentos, vendedor, estimados y media IAAI.

La suite .NET combinada aprobó 64 pruebas. La suite UI aprobó 51 pruebas antes de la integración del adaptador, que no modifica el cliente.

## Producción actualmente servida

El despliegue federado terminó correctamente el 26 de agosto de 2026. ACR construyó y publicó `acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:iaai-extended-afaac3d` con digest `sha256:1757523fc52945d5cdae8c88df7dab1e4ce865b668b5e216165f17fef7cd1510`. La API quedó en la revisión `ca-lsc-inventory-api-prod--0000012`, estado `Running`, y el job manual `job-lsc-iaai-pilot-prod` quedó configurado con la misma imagen.

Se inició exactamente una ejecución manual IAAI posterior al despliegue. El bridge público confirmó 1,000 vehículos IAAI desde PostgreSQL, cero vehículos Copart y una actualización de corte a las `2026-08-26T18:34:33Z`. La interfaz publicada cargó los 1,000 lotes, de los cuales 948 quedaron visibles con la política predeterminada de títulos especiales.

| Campo o control | Cobertura observada en 1,000 IAAI | Resultado |
|---|---:|---|
| Fotos | 1,000 | Disponible; 978 también incluyen media tipada. |
| Daño primario y odómetro | 1,000 / 1,000 | Disponible y filtrable. |
| Vendedor y tipo de vendedor | 1,000 / 1,000 | Disponible; 875 valores se etiquetan `unknown` cuando el proveedor no identifica más detalle. |
| Daño secundario | 427 | Disponible cuando el proveedor lo reporta. |
| Documento de venta | 848 | Disponible cuando el proveedor lo reporta. |
| Motor | 828 | Disponible por descripción, litros o potencia. |
| Estimado proveedor | 848 | Disponible cuando existe rango o texto del proveedor. |
| Llaves y Buy Now | 1,000 / 1,000 | Campos presentes en el contrato; valores se presentan honestamente cuando no hay importe o condición aplicable. |
| Layout de motor | 899 | Disponible cuando el proveedor lo reporta. |
| ACV / reparación estimada | 963 / 334 | Campos parciales según disponibilidad del proveedor. |
| URLs de media no HTTPS | 0 | No se expusieron URLs inseguras. |

Las diferencias remanentes son de fuente, no de contrato: `lossType` permanece no reportado para este corte; algunos vendedores, motores, estimados, daños secundarios y documentos no son provistos por IAAI en cada lote. Los títulos especiales permanecen almacenados y seleccionables; 26 se detectaron en el corte, pero el UI los oculta por defecto según la política vigente.

## Bloqueo externo y reintentos observados

La CLI local continúa sin sesión: `az account show` respondió `Please run 'az login' to setup account.` La sesión Azure controlable del navegador pasó después a reportar `There is no internet connection`, por lo que ya no permite observar ni confirmar un estado productivo.

Se preparó y autorizó una ruta federada de mínimo privilegio con identidad asignada por usuario. Sus dos primeros intentos fallaron antes de actualizar la API o el job: el runtime de Azure Deployment Scripts no contiene `tar`, y ACR no pudo descargar el repositorio GitHub consolidado porque no es público para un clonador anónimo. La corrección usó un paquete HTTPS inmutable del Engine cuyo SHA-256 se verificó localmente y el último reintento fue exitoso. La vía de integración GitHub-Azure visual no quedó operativa porque intenta realizar operaciones de credenciales de ACR mientras el usuario administrador del registro permanece deshabilitado; el mecanismo verificado es ARM/Deployment Scripts con identidad federada, no Cloud Shell ni credenciales administrativas. Copart no fue reactivado dentro de Apibara.

## Controles operativos pendientes

Cloud Shell no se recuperó como una sesión estable y observable; no debe ser requisito para la operación. La siguiente sincronización deberá ser una nueva decisión operativa, con límite de enriquecimiento explícito y verificación posterior del bridge; no se ejecuta automáticamente desde este cierre. Copart debe permanecer exclusivamente en el adaptador Excel.

## Disposición final de rutas de despliegue

| Ruta | Estado | Decisión operativa |
|---|---|---|
| Cloud Shell | Descartada para este despliegue | Se desconectó incluso en preflight; no ejecutó el despliegue exitoso y continúa sin una sesión estable observable. |
| Asistente visual GitHub-Azure | Descartada | Requiere operaciones de credenciales administrativas de ACR; el usuario administrador del registro permanece deshabilitado. |
| Contexto Git directo de ACR | Descartado | El repositorio consolidado no era accesible para el clonador anónimo de ACR. No se añadieron tokens ni credenciales al contexto. |
| ARM + Deployment Scripts + identidad federada + paquete HTTPS SHA verificado | **Validada** | Fue la ruta que construyó, publicó y aplicó la imagen IAAI ampliada. |

## Estado verificado el 27 de agosto de 2026

Los despliegues federados r5b, r5c y r5d finalizaron correctamente en `rg-lsc-inventory-prod`. La API actual expone el resumen y la búsqueda paginada a través del bridge server-side, sin enviar el token Azure al navegador. La última lectura verificada por bridge reportó **62,258 lotes activos**: **58,836 Copart** ingresados por Excel y **3,422 IAAI** ingresados exclusivamente por Apibara. La búsqueda predeterminada devuelve 20 resultados y los títulos especiales se ocultan mediante un flag server-side, no mediante una lista extensa en la URL.

El despliegue `CustomDeployment-20260827095302` finalizó correctamente y ejecutó el PATCH de programación IAAI. El script falla si no encuentra primero la imagen `iaai-national-r3-full-backfill` o si no confirma el trigger, cron, timeout, paralelismo, completion count e imagen final. Por esa condición de éxito, queda verificada la configuración siguiente; la primera ejecución automática todavía debe observarse en Execution history.

| Fuente | Trigger / cron UTC | Inicio cada hora | Controles | Estado |
|---|---|---:|---|---|
| IAAI / Apibara | `Schedule` · `15,45 * * * *` | `:15`, `:45` | 1 réplica, timeout 1,500 s, retry 1, completion count 1, lease 28 min | **Activado** |
| Copart / Excel | Debe configurar la tarea de importación | `:00`, `:30` recomendado | Debe mantener su propio control de solape | Pendiente de la tarea `Uploaded Copart Files` |

La separación de 15 minutos evita arranques simultáneos. No es un bloqueo cruzado: si una ejecución supera 15 minutos, ambos procesos podrían convivir temporalmente. La regla operativa es revisar las primeras ejecuciones y añadir un lease compartido o ampliar el desfase si cualquiera supera 10 minutos. Copart continúa **prohibido** como fuente Apibara.

Referencia operativa: [Jobs in Azure Container Apps — cron de cinco campos evaluado en UTC](https://learn.microsoft.com/en-us/azure/container-apps/jobs).

## Paquete preparado para la mañana siguiente

El reporte protegido de PostgreSQL confirmó inicialmente 62,481 lotes y la lectura posterior a los despliegues reportó **62,258 activos**. La diferencia corresponde al ciclo de vida/reconciliación de snapshots; el portal siempre muestra el total actual entregado por la consulta, no un conteo codificado. La interfaz heredada solo podía solicitar 1,000 y luego ocultaba parte de ese subconjunto por filtros de cliente; por eso 989 visibles no era el total de la base. La revisión desplegada sustituye ese comportamiento por búsqueda y paginación en PostgreSQL, muestra 20 resultados iniciales y obtiene el total y las facetas mediante el bridge server-side.

| Componente | Estado local | Estado en Azure |
|---|---|---|
| Conteo total y facetas | Desplegado; modelos se recalculan al escoger marca | API r5d verificada por bridge |
| Todos los filtros visibles | Contrato y pruebas cubren fuente, subasta, precio, año, ubicación, condición, documentos, mecánica y media | API r5d verificada por bridge |
| Resultados paginados | Desplegado con 20 filas por página y total server-side | Bridge: páginas 1 y 2 verificadas |
| Identificación de plataforma | Badges accesibles con logo Copart / IAAI en lista y ficha | Publicado en checkpoint `a7bb435b` |
| Job IAAI y cron | Imagen r3 conservada | Schedule `15,45 * * * *` activado |
| Copart / Apibara | Sin cambios | Copart exclusivo por Excel |

Las plantillas r5b/r5c/r5d ya fueron aplicadas y el checkpoint `a7bb435b` publicó la interfaz. La activación de IAAI se aplicó de forma independiente con la plantilla r3 escalonada. El siguiente control es observar una ejecución automática en Execution history, sin lanzar `Run now` ni repetir backfill a ciegas.
