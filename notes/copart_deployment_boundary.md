# Frontera de despliegue Copart

## Regla operativa vigente

Las automatizaciones de esta tarea solo pueden construir imágenes con etiqueta `copart-<commit>` y crear o actualizar el Job manual `job-lsc-copart-excel-prod`.

No pueden ejecutar `az containerapp update`, modificar `ca-lsc-inventory-api-prod`, construir ni publicar etiquetas `api-*`, modificar `job-lsc-iaai-pilot-prod`, ni reactivar o modificar `job-lsc-inventory-ingestion-prod`. Tampoco pueden actualizar Jobs programados de Copart desde esta tarea.

## Verificación obligatoria

Antes y después de cada ejecución del workflow `copart-azure-deploy-and-run.yml`, el workflow debe consultar en modo de solo lectura la imagen de `ca-lsc-inventory-api-prod`. Si cambia, el workflow debe fallar.

## Imagen base protegida

| Momento | Aplicación | Revisión | Imagen |
|---|---|---|---|
| 2026-08-28 15:36 UTC | `ca-lsc-inventory-api-prod` | `ca-lsc-inventory-api-prod--0000031` | `acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:api-c4534db00aff42967f33411349fae469d3e83d5d` |

La imagen anterior es un dato de control, no una instrucción para desplegarla o modificarla desde esta tarea.
