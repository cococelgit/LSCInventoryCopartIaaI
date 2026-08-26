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
