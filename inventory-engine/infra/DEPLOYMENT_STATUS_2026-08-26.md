# Estado de despliegue — 2026-08-26

## Código preparado

El monorepo contiene un Engine integrado que combina:

- Ingesta IAAI mediante Apibara, con mezcla lista/detalle y enriquecimiento controlado.
- Adaptador Copart Excel en streaming, separado de Apibara.
- Políticas determinísticas de limpieza, elegibilidad, auditoría y reconciliación.
- Contrato público ampliado para condición, documentos, vendedor, estimados y media IAAI.

La suite .NET combinada aprobó 64 pruebas. La suite UI aprobó 51 pruebas antes de la integración del adaptador, que no modifica el cliente.

## Producción actualmente servida

El bridge público responde 1,000 vehículos IAAI. La versión desplegada ya expone plataforma, color y Buy Now cuando se reportan, pero todavía no expone los campos extendidos del contrato nuevo, como vendedor, body style, motor, estimado del proveedor, llaves, daños primario/secundario ni nombre de documento.

## Bloqueo externo y reintentos observados

La CLI local continúa sin sesión: `az account show` respondió `Please run 'az login' to setup account.` La sesión Azure controlable del navegador pasó después a reportar `There is no internet connection`, por lo que ya no permite observar ni confirmar un estado productivo.

Se preparó y autorizó una ruta federada de mínimo privilegio con identidad asignada por usuario. Sus dos primeros intentos fallaron antes de actualizar la API o el job: el runtime de Azure Deployment Scripts no contiene `tar`, y ACR no pudo descargar el repositorio GitHub consolidado porque no es público para un clonador anónimo. La corrección posterior utiliza un paquete HTTPS inmutable del Engine cuyo SHA-256 se verificó localmente. El reintento con ese paquete se inició, pero su resultado no puede afirmarse sin recuperar una sesión Azure autenticada y observable. No se inició una ejecución IAAI durante estos intentos; Copart no fue reactivado dentro de Apibara.

## Acción pendiente tras reautorización

Una vez recuperada la sesión observable, primero se debe consultar el Deployment Script `run-iaai-extended-afaac3d`, el tag `lsc-inventory-engine:iaai-extended-afaac3d`, la revisión de `ca-lsc-inventory-api-prod` y la imagen configurada de `job-lsc-iaai-pilot-prod`. Solo si esos cuatro controles confirman la imagen nueva se debe ejecutar una única sincronización IAAI enriquecida y medir los campos mediante el bridge LSC. Copart debe permanecer exclusivamente en el adaptador Excel.
