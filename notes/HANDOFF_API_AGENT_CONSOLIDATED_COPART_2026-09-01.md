# Handoff consolidado — API/Portal e Inventory Engine para Copart

**Fecha:** 1 de septiembre de 2026
**Audiencia:** agente responsable del API interno/público, bridge y portal de inventario.
**Repositorio:** `cococelgit/LSCInventoryCopartIaaI` · rama `main`.

> **Objetivo:** hacer que el API y el portal consuman correctamente los datos ya producidos por la ingesta Copart, manteniendo una sola fuente de verdad, filtros consistentes y comunicación comercial conservadora. Este documento no autoriza modificar la ingesta, los jobs, el cron ni IAAI.

---

## 1. Inicio obligatorio y frontera de trabajo

Antes de cambiar código, sincroniza la rama y toma evidencia **de solo lectura** de la revisión de API que atiende producción. No asumas que una imagen o baseline antiguo continúa vigente: otros agentes pueden haber desplegado cambios de API en paralelo.

```bash
git pull --ff-only origin main
```

| Dominio | Responsable de este handoff | Prohibiciones |
|---|---|---|
| API de inventario y bridge | API agent | No modificar la lógica de carga Copart, jobs, cron, secretos, identidad ni Blob. |
| Portal | API/portal agent | No usar mocks, snapshots locales, `recent?take=1000` ni filtrado de navegador para simular inventario activo. |
| Copart | CSV/CSV.GZ ya descargado en Azure | Nunca usar Apibara para Copart. |
| IAAI | Apibara/Open | No usar datos o taxonomías Copart para IAAI. |
| Base de datos | Consultas y contratos de lectura | No aplicar migraciones generales ni backfills que inventen historia o estado. |

El job automático de Copart quedó operando con su imagen validada y el cron actual en UTC:

```text
0 0,2,12,14,16,18,20,22 * * *
```

Ese cron corresponde, mientras Miami está en EDT, a 8:00 a.m., 10:00 a.m., 12:00 p.m., 2:00 p.m., 4:00 p.m., 6:00 p.m., 8:00 p.m. y 10:00 p.m. El scheduler se gestiona fuera de esta tarea de API; **no lo modifiques**.

### Matriz de estado del productor Copart

| Capacidad | Estado en ingesta/datos | Acción del API/portal |
|---|---|---|
| Separación de fuentes | Copart = CSV/CSV.GZ en Azure; IAAI = Apibara/Open. | Preservar plataforma y no mezclar fuentes. |
| Limpieza y elegibilidad | Se ejecutan antes de persistir; D09 sigue desactivada. | Consumir decisión/datos persistidos; no re-elegir ni crear descartes por título. |
| Lifecycle | Activo tras presencia; baja después de tres snapshots completos ausentes; reaparición reactiva. | Aplicar `is_active = true` de forma uniforme. |
| Candado de procesamiento | Lease PostgreSQL Copart-only; `duplicate` y `skipped_lock_held` son no-ops exitosos. | Mostrar estado auditable y `N/D` donde no hubo procesamiento por fila. |
| Métricas de cambios | `created_count`, `updated_count`, `unchanged_count` en manifiesto Copart completo. | Unir por `run_id` para el Centro de Ejecuciones. |
| Buy Now | Solo precio fuente estrictamente mayor que cero. | Filtro, badge y contador global usan `buy_now_usd > 0`. |
| Run & Drive | Valores raw y normalizados separados de tracción. | Exponer, filtrar y mostrar disclaimer; no inferir. |
| Títulos | Taxonomía canónica + fuente raw preservada; no causa descarte automático. | Leer campos canónicos; no crear clasificador paralelo. |
| Score v2 | Código disponible, pero su promoción/backfill requiere compatibilidad previa de API. | Soportar `PRE_GRADED_WITH_FLAGS` antes de que ingesta lo promueva. |
| Historial de subastas | Observaciones, intentos y señales se derivan solo con evidencia suficiente. | Exponer con copy cauteloso; nunca inferir no-venta por ausencia. |
| Media | Se consumen exclusivamente proxies del API. | No exponer origen Copart ni reactivar procesos de media. |

---

## 2. Fuente de verdad y disponibilidad activa — prioridad P0

El portal debe usar el bridge paginado `inventory.browse` como fuente única de inventario. La búsqueda, el total, las facetas, los relacionados y la ficha deben partir de la **misma población**:

```sql
is_active = true
```

La condición debe aplicarse en PostgreSQL **antes** de filtros, conteos, ordenamiento y paginación. Un lote inactivo no puede reaparecer por búsqueda, título, historial, media, score, related lots, facetas o URL directa.

| Superficie | Regla obligatoria |
|---|---|
| `inventory.browse` | Solo lotes activos; total y páginas calculados sobre activos. |
| Búsqueda exacta y parcial | Misma condición de activos antes de paginar. |
| Facetas / resumen | Misma población y mismos filtros que `browse`. |
| Ficha | Si el lote está inactivo, responder no disponible; el portal muestra **“Este vehículo ya no está disponible en inventario.”** |
| Relacionados | Nunca usan un lote inactivo como candidato visible. |

Existe un caso de regresión previamente verificado: un lote Copart inactivo con tres ausencias consecutivas llegó a aparecer en búsqueda mientras su ficha exacta devolvía no disponible. No reproduzcas esa desalineación. [1]

### Criterios de aceptación P0

1. Un lote `is_active = false` no aparece en lista, búsqueda, facetas, total, páginas, relacionados ni ficha activa.
2. El `total` y las facetas se calculan con `is_active = true` antes del `LIMIT/OFFSET`.
3. El portal no intenta corregir la disponibilidad ocultando tarjetas localmente.
4. Las pruebas cubren consistencia entre búsqueda y ficha.

---

## 3. Ejecuciones Copart: nuevos, actualizados y sin cambio — prioridad P0

### Estado del productor

Los contadores por snapshot Copart ya se guardan al terminar un snapshot completo y exitoso. No son conteos de grading.

| Tabla | Campos | Semántica |
|---|---|---|
| `copart_snapshot_manifests` | `created_count`, `updated_count`, `unchanged_count` | Resultado real de comparar `lot_key` y `payload_hash` durante esa corrida. |
| `inventory_sync_runs` | `run_id`, proveedor, estado, timestamps y conteos genéricos | Bitácora que alimenta la pantalla de ejecuciones. |

La unión es por `run_id`. **No agregues ni escribas columnas nuevas en `inventory_sync_runs` desde el job Copart**: los jobs productivos se ejecutan con migraciones generales desactivadas. El intento de hacerlo fue retirado antes de que afectara una carga.

### Consulta de lectura requerida

Para registros `provider = 'copart-excel'`, une el manifiesto:

```sql
left join copart_snapshot_manifests manifest
  on manifest.run_id = run.run_id
```

Expón los contadores solamente si el run representa un snapshot Copart completo y exitoso:

```sql
case when run.provider = 'copart-excel'
          and run.state_scope = 'all'
          and manifest.is_complete = true
          and manifest.status = 'succeeded'
     then manifest.created_count
     else null
end as created
```

Aplicar la misma condición a `updated` y `unchanged`.

| Tipo de ejecución | `created` / `updated` / `unchanged` en UI |
|---|---|
| Snapshot Copart completo y exitoso | Mostrar valores reales. |
| `duplicate` | `N/D`; se validó el archivo, pero no se persistió fila por fila. |
| `skipped_lock_held` | `N/D`; no abrió el archivo, no persistió ni reconcilió. |
| Snapshot inválido, parcial o con errores | `N/D`; no exponer conteos parciales como resultado final. |
| IAAI u otro proceso | Conservar contrato existente; no derivar valores Copart. |

No convertir `NULL` a cero. `0` significa una corrida válida que realmente no creó/actualizó/dejó sin cambio lotes; `N/D` significa que la métrica no aplica. [2]

---

## 4. Contratos Copart ya producidos por la ingesta

### 4.1 Identidad, versiones y lifecycle

- Identidad de un lote Copart: `copart:<lot-number>`.
- Un lote que reaparece no se duplica: se compara contra su payload; puede quedar `created`, `updated` o `unchanged`.
- El historial de payloads se conserva en `auction_lot_versions` y los payloads/auditoría en Blob privado.
- Un lote se desactiva solo tras **tres snapshots completos consecutivos** ausente; si reaparece, se reactiva.
- Un archivo duplicado, parcial, corrupto, inválido o una corrida con `skipped_lock_held` no puede reconciliar ni despublicar lotes.

La capa de API debe representar estado actual; no debe recrear lifecycle desde timestamps del navegador.

### 4.2 Buy Now

Un lote tiene Buy Now exclusivamente si:

```text
buy_now_usd > 0
```

`NULL`, cero, texto inválido o ausencia de precio significan **no disponible**. Un bid actual de cero no implica Buy Now ni debe confundirse con esta condición.

El resultado paginado de `inventory.browse` debe devolver un contador global calculado sobre todos los filtros activos, no sobre las tarjetas de la página:

```ts
type InventoryBrowsePage = {
  page: number;
  pageSize: number;
  total: number;
  totalPages: number;
  vehicles: Vehicle[];
  filterCounts: { buyNowCount: number };
};
```

Sea `F` el conjunto de lotes activos que cumple plataforma, búsqueda y todos los filtros salvo página, orden y el propio switch Buy Now. El valor obligatorio es:

```text
buyNowCount = COUNT(F donde buy_now_usd > 0)
```

El contador no cambia al activar `buyNowOnly`; al activar el switch, sí cambian la lista y el `total` a `COUNT(F donde buy_now_usd > 0)`. La consulta debe compartir un CTE base o constructor de filtros con browse, total y facetas. [3]

### 4.3 Run & Drive

La fuente es únicamente la columna Copart `Runs/Drives`. No confundirla con `Drive` / `driveType`, que significa tracción.

| Campo público | Tipo | Regla |
|---|---|---|
| `runCondition` | `RUNS_AND_DRIVES` \| `STARTS` \| `STATIONARY` \| `UNVERIFIED` | Valor operativo normalizado. |
| `runConditionRaw` | `string \| null` | Texto fuente conservado para transparencia. |
| `driveType` | `string \| null` | Tracción; no se usa para inferir condición de marcha. |

Normalización permitida:

| Valor fuente, sin distinguir mayúsculas | Valor normalizado |
|---|---|
| `RUN & DRIVE`, `RUNS AND DRIVES` | `RUNS_AND_DRIVES` |
| `STARTS`, `ENGINE START PROGRAM` | `STARTS` |
| `STATIONARY` | `STATIONARY` |
| Vacío, `NO INFORMATION` o desconocido | `UNVERIFIED` |

La UI puede usar una faceta por `runCondition`, mostrar `runConditionRaw` solo en ficha/tooltip y debe incluir el disclaimer:

> “Run & Drive es la condición declarada por Copart; no garantiza que el vehículo funcione al recogerlo, sea seguro para conducir o no requiera reparación.”

No inferir este estado desde llaves, daños, título, score, fotos o historial. [4]

### 4.4 Taxonomía canónica de títulos

El productor aplica una única autoridad canónica: `TitleFacetCategory`. El API y el portal **leen** estos metadatos del payload vigente; no los recalculan por texto, regex, frontend ni fuentes externas.

| Campo de payload Copart | Contrato API esperado |
|---|---|
| `title_category` | `titleCategory` |
| `title_flags` | `titleFlags` |
| `title_review_status` | `titleReviewStatus` |
| `title_taxonomy_version` | `titleTaxonomyVersion` |
| Código/descripción fuente y notas | Conservar como evidencia; no sustituirlos. |

Valores canónicos actuales:

```ts
titleCategory: "CLEAN" | "SALVAGE" | "REBUILT" | "SPECIAL" | "UNVERIFIED" | "OTHER" | null;
titleFlags: string[];
titleReviewStatus: "CLASSIFIED" | "UNVERIFIED" | "REVIEW_REQUIRED" | null;
titleTaxonomyVersion: string | null;
```

Para IAAI: `titleCategory: null`, `titleFlags: []`, `titleReviewStatus: null`, `titleTaxonomyVersion: null`. No clasificar IAAI con la tabla Copart.

La taxonomía no activa D09, no descarta ni despublica por título y no certifica registro, circulación, exportación o importación. Aplicar el filtro/faceta sobre activos y en PostgreSQL antes de ordenar/paginar. [5]

### 4.5 Scoring y pre-grado

El API debe **consumir el resultado canónico persistido**, nunca recalcular score desde payload crudo. Debe soportar simultáneamente la versión v1 y v2:

```ts
type VehicleScore = {
  status: "PRE_GRADED" | "PRE_GRADED_WITH_FLAGS" | "MANUAL_REVIEW" | "NEEDS_ENRICHMENT" | "DISCARDED";
  preGrade: number | null;
  buyScore: number | null;
  coveragePercent: number;
  confidencePercent: number;
  reasonCodes: string[];
  missingFields: string[];
  policyVersion: "lsc_pre_grade_v1" | "lsc_pre_grade_v2";
  inputHash: string;
  scoredAt: string;
};
```

| Estado | API/UI correcta |
|---|---|
| `PRE_GRADED` | Nota numérica estándar. |
| `PRE_GRADED_WITH_FLAGS` | Nota numérica **provisional** + confianza + badges explicables; ordenar detrás de `PRE_GRADED`. |
| `MANUAL_REVIEW`, `NEEDS_ENRICHMENT` | Nota `null`; no fabricar una. |
| `DISCARDED` | Excluir de ranking comercial y de inventario activo. |

Cuando exista `PRE_GRADED_WITH_FLAGS`, no mostrar “sin grading”. Usar “Pre-grado provisional” y el disclaimer de datos declarados por subasta. No afirmar `Runs & Drives` cuando hay bandera de condición no confirmada.

> **Dependencia:** la política Copart v2 debe ser soportada por API/portal antes de cualquier promoción o backfill de esa política por el responsable de ingesta. No asumas que v2 ya está activa solo porque el código existe en `main`. [6]

### 4.6 Historial de intentos de subasta y señal de oportunidad

Usar `lot_key`, no VIN, para unir el historial.

| Tabla | Uso API/UI |
|---|---|
| `copart_lot_observations` | Evidencia inmutable de presencia por lote y snapshot completo. |
| `copart_auction_attempts` | Línea de tiempo consolidada por `lot_key` y `auction_at`. |
| `copart_lot_motivation_signals` | Resumen de prioridad y componentes explicables. |

Resultados permitidos y copy:

| `outcome` | Copy correcto |
|---|---|
| `scheduled` | “Programado / pendiente. Resultado aún no confirmado.” |
| `sold_confirmed` | “Venta reportada por Copart.” Solo si `sale_price_usd > 0`. |
| `relisted_inferred` | “Re-listado inferido. El lote reapareció con una nueva fecha; esto no confirma el resultado anterior.” |
| `unknown` | “Resultado no confirmado.” |

Una desaparición nunca significa no vendido. No usar “vendedor obligado”, “vendedor motivado” como hecho, ni “el carro no se vendió” sin evidencia explícita.

La señal de oportunidad es prioridad operativa, no pronóstico. Si no hay al menos un `relisted_inferred`, su score debe ser cero. También es cero con venta confirmada. Un lote inactivo no se vuelve visible por tener historial o score. [7]

### 4.7 Fotos y media

El portal consume solo las URLs proxy entregadas por el API. Nunca construye, registra ni expone URLs directas de Copart, signed URLs, query tokens o catálogos privados. La falta de fotos no debe reconstruirse desde el navegador ni reactivar media jobs desde la tarea de API.

---

## 5. Backlog obligatorio y orden recomendado

### P0 — consistencia y operación

| Orden | Entregable | Criterio de salida |
|---:|---|---|
| 1 | Activos coherentes en browse, búsqueda, facetas, total, relacionados y ficha | Misma población `is_active = true` antes de paginar; regresión cubierta. |
| 2 | Estabilizar `inventory.browse` | Sin HTTP 500 ni timeout de carga inicial; medir consulta representativa y evitar filtros/postprocesos en cliente. |
| 3 | Contadores de ejecuciones Copart | `LEFT JOIN` por `run_id` a manifiestos; valores reales o `N/D` con semántica correcta. |

### P1 — filtros, contratos y presentación

| Orden | Entregable | Criterio de salida |
|---:|---|---|
| 4 | Contador global Buy Now | `filterCounts.buyNowCount` desde servidor; `buy_now_usd > 0`; coherente con todos los filtros. |
| 5 | Run & Drive | Contrato, faceta y copy/disclaimer; no confundir con tracción. |
| 6 | Títulos canónicos | Contrato, faceta y flags desde payload; sin clasificador paralelo ni descarte D09. |
| 7 | Contrato de score v1/v2 | Soporte de `PRE_GRADED_WITH_FLAGS` antes de aprobar promoción v2. |

### P2 — inteligencia comercial con evidencia

| Orden | Entregable | Criterio de salida |
|---:|---|---|
| 8 | Historial y señal de oportunidad | Endpoint/bridge protegido, timeline con evidencia y copy cauteloso. |
| 9 | Filtros de oportunidad | Solo niveles permitidos; no mostrar inactivos ni inferir venta/no venta. |
| 10 | Galería/ficha Copart | Solo URLs proxy y estados honestos para media incompleta. |

---

## 6. Rendimiento, diseño de consulta y seguridad

1. Construir una población base filtrada en PostgreSQL y reutilizarla para resultados, total, facetas y contadores. No aplicar `LIMIT/OFFSET` antes de disponibilidad o filtros.
2. Resolver búsquedas exactas de lote y VIN con parámetros enlazados. El VIN público debe permanecer enmascarado; no exponer VIN completo, seller data no autorizado ni payload crudo.
3. Si se requiere un nuevo índice, entregar antes `EXPLAIN (ANALYZE, BUFFERS)` y solicitar aprobación antes de aplicar una migración.
4. Preferir contratos internos autenticados para auditoría, ejecución e historial. Separar los datos de operación de los endpoints públicos de inventario.
5. No usar una caché histórica/local para reemplazar una consulta correcta. Si existe caché, debe respetar la misma población activa y estrategia de invalidación.

---

## 7. Pruebas mínimas que debe entregar el agente

| Área | Casos obligatorios |
|---|---|
| Disponibilidad | Inactivo ausente de búsqueda/facetas/total/ficha/relacionados; activo presente bajo mismos filtros. |
| Paginación | Total/facetas calculados antes de página; página vacía mantiene conteos correctos. |
| Buy Now | `NULL` y 0 no cuentan; contador global se mantiene con switch encendido/apagado. |
| Run & Drive | Valores explícitos, `UNVERIFIED`, raw conservado y `driveType` separado. |
| Títulos | Código known/unknown, IAAI nulo, categoría filtrada sobre activos; sin descarte automático. |
| Score | V1 y v2 coexistentes; `PRE_GRADED_WITH_FLAGS` con score numérico; descartados fuera del ranking. |
| Ejecuciones Copart | Completo exitoso muestra tres conteos; duplicado/lock/partial muestran `N/D`. |
| Historial | `unknown` no se vuelve no-vendido; `relisted_inferred` muestra disclaimer; inactivo no reaparece. |
| Seguridad | Sin VIN completo, URLs Copart directas, tokens ni payloads crudos en respuestas o logs UI. |

---

## 8. Límite de despliegue y reporte final del agente

El agente de API/portal trabaja en **API/bridge/portal solamente**, bajo su propia autorización de despliegue. Antes y después debe registrar de forma segura:

1. revisión e imagen real del API antes y después;
2. que los jobs Copart e IAAI, cron, imágenes de jobs, identidad y secretos no cambiaron;
3. que no se inició `Run now` para Copart;
4. commit SHA, pruebas y evidencia de los criterios de aceptación;
5. contrato público final y ejemplo JSON sanitizado.

No usar `az containerapp update` sobre una Container App como atajo. Si el cambio requiere API, seguir el workflow/release API aprobado por el propietario de esa capa. No publicar una imagen API ni tocar la infraestructura de ingesta desde este handoff.

---

## 9. Referencias internas

[1] [Handoff general de Copart para portal](HANDOFF_PORTAL_AGENT_COPART.md)
[2] [Contadores de ejecución Copart](HANDOFF_PORTAL_AGENT_COPART_EXECUTION_COUNTERS.md)
[3] [Especificación del contador global Buy Now](../PROMPT_API_AGENT_BUY_NOW_GLOBAL_COUNTER.md)
[4] [Contrato Run & Drive](COPART_RUN_AND_DRIVE_CONTRACT.md)
[5] [Taxonomía canónica vigente en código](../inventory-engine/src/Lsc.Inventory.Api/Normalization/TitleFacetCategory.cs) y [mapeador Copart](../inventory-engine/src/Lsc.Inventory.Api/Sources/CopartTitleMapper.cs)
[6] [Contrato API para Copart Pre-Grade v2](../PROMPT_API_AGENT_COPART_PRE_GRADE_V2.md)
[7] [Handoff de historial de subasta Copart](HANDOFF_PORTAL_AGENT_COPART_AUCTION_HISTORY.md)
