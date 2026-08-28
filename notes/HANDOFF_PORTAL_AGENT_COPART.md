# Handoff para agente del portal — Copart / Inventory Engine

## Objetivo de tu tarea

Tu responsabilidad es corregir **solo la capa de portal/bridge** para que consuma el inventario activo y los campos ya expuestos por la API de Inventory Engine. No modifiques la ingesta de Copart, el flujo IAAI, los Jobs Azure ni los workflows de despliegue de esta tarea.

## Fuente de verdad

El portal debe leer el inventario mediante el endpoint paginado ya disponible desde el bridge `inventory.browse`. No debe depender de `inventory.recent?take=1000`, snapshots locales, caché histórica ni resultados previamente descargados.

La lista y la ficha deben mostrar solamente lotes con estado activo. Si un lote deja de estar disponible, la ficha debe retirarlo de la lista o mostrar claramente **“Vehículo ya no disponible”**; no debe seguir mostrándose como inventario activo.

### Ejemplo que prueba el problema

El lote Copart `48826366`, mostrado en una tarjeta con una miniatura y campos `N/R`, fue verificado en PostgreSQL como:

| Campo | Valor verificado |
|---|---|
| Plataforma | `copart` |
| Estado actual | `is_active = false` |
| Ausencias consecutivas | `3` |
| Última observación | 2026-08-28 01:29 UTC |
| Fotos persistidas | 1 miniatura histórica |

No es un vehículo que deba aparecer en el inventario activo. Su presencia en el portal demuestra que la pantalla está consumiendo un dataset antiguo o no está aplicando la condición de activo.

## Contrato de datos ya disponible

Para Copart, el Inventory Engine tiene mapeados y entrega de forma segura:

| Campo | Regla de presentación |
|---|---|
| VIN | Solo VIN enmascarado; nunca mostrar el VIN completo. |
| Vendedor | Mostrar solo cuando Copart lo informa; en caso contrario, “No informado por Copart”. |
| Título | Mostrar código original, descripción en inglés/español, estado y estado de mapeo. |
| Especificaciones | Trim, carrocería, motor, cilindros, odómetro, tracción, transmisión y combustible. |
| Costos y condición | Valor retail estimado, costo de reparación, condición de lote, llaves y `Runs/Drives`. |
| Fotos | La API entrega URLs de proxy de La Subasta Cubana para fotos Copart; la URL/origen Copart y parámetros no deben mostrarse ni registrarse en frontend. |

Los títulos Copart existentes fueron normalizados en PostgreSQL. El mapeo usa un catálogo de 181 códigos; los 453 códigos sin referencia oficial se conservan como `unmapped`, sin inventar descripción y sin convertirlos en descarte. La regla D09 sigue desactivada.

## Estado de inventario y media

La base activa que auditó esta tarea tenía 59,045 lotes Copart: 58,705 con dos o más fotos, 340 con una sola y cero sin foto. El Job de enriquecimiento de media se encuentra pausado por un defecto de resolución de algunos catálogos; no lo reactives desde la tarea del portal. La pantalla debe usar las fotos que entregue la API para lotes activos; no debe intentar construir o exponer URLs directas de Copart.

## Límite estricto de despliegue

**No actualizar `ca-lsc-inventory-api-prod`.** Su imagen protegida es:

```text
acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:api-c4534db00aff42967f33411349fae469d3e83d5d
```

No ejecutar `az containerapp update`, no publicar imágenes `api-*`, no tocar los Jobs Copart o IAAI, y no restaurar los workflows eliminados:

- `.github/workflows/inventory-api-azure-deploy.yml`
- `.github/workflows/copart-azure-schedule.yml`
- `.github/workflows/copart-azure-media-control.yml`

La única automatización mutable que quedó autorizada desde esta tarea es el Job manual `job-lsc-copart-excel-prod`; no la uses desde la tarea del portal. Los diagnósticos Copart son de solo lectura.

## Commits y orden de trabajo

Primero ejecutar:

```bash
git pull --ff-only origin main
```

El compromiso de frontera de despliegue es `52df1b0`. La nota de la frontera está en `notes/copart_deployment_boundary.md`.

Después, enfócate en conectar/publicar el portal existente contra `inventory.browse`, aplicar filtro activo del lado del servidor y renderizar los campos ya presentes. Si el portal se despliega desde otro proyecto Manus, trabaja allí; no crees un dominio nuevo ni cambies la API Azure.

## Criterios de aceptación del portal

1. La lista informa el total real paginado y no se corta a 1,000.
2. Un lote inactivo como `48826366` no aparece en búsqueda/listado activo.
3. Las tarjetas y la ficha consumen VIN enmascarado, título, vendedor condicional y especificaciones reales.
4. La galería usa solo las URLs que devuelve la API, sin URLs Copart directas ni query strings de origen.
5. El despliegue del portal no cambia la imagen de `ca-lsc-inventory-api-prod`.
