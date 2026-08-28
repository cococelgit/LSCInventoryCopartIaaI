# Handoff — Historial de intentos de subasta Copart para UI

**Estado:** implementado en la ingesta Copart por archivos descargados y verificado mediante ejecución manual. **Este documento no autoriza despliegues del API o portal.** El agente de UI/API debe implementar su propia capa de lectura y su propio despliegue dentro de su límite operativo.

## 1. Propósito y regla de interpretación

Esta funcionalidad construye un historial auditable por lote Copart para priorizar unidades que **podrían** representar una oportunidad de negociación. La señal describe el comportamiento observado en el listado de Copart; no prueba la intención, presión financiera ni disposición final del vendedor.

> Texto comercial permitido: **“Señal de oportunidad basada en historial de subasta.”**
>
> Texto prohibido: “vendedor obligado a vender”, “el carro no se vendió” o cualquier afirmación de resultado basada solamente en que el lote dejó de aparecer.

El motor solo puede marcar un intento anterior como `relisted_inferred` cuando el mismo lote reaparece en un **snapshot completo posterior** con una fecha de subasta posterior y esa reaparición ocurre después de la fecha anterior. Una desaparición nunca se traduce en no venta. La única venta confirmada por esta versión es un precio de venta positivo reportado por el origen. [1]

| Alcance | Confirmado |
|---|---|
| Fuente | Solo `platform = copart`, procedente del CSV/CSV.GZ descargado y procesado por el servidor. |
| IAAI / Apibara | No participa ni se modifica. |
| Inventario, elegibilidad, lifecycle, títulos y media | No fueron modificados por esta funcionalidad. |
| API público y portal | No se añadió endpoint, contrato ni cambio de UI. |
| Datos sensibles | No se exponen VINs, URLs de Copart, payloads crudos ni datos de vendedor en este contrato. |

## 2. Clave de unión y tablas

Usar `lot_key` como clave estable. Para Copart tiene normalmente la forma `copart:<lot-number>`. **No unir por VIN**, pues el historial es del lote de subasta y el VIN no forma parte de esta funcionalidad.

| Tabla | Cardinalidad | Finalidad | Clave / acceso recomendado |
|---|---:|---|---|
| `copart_lot_observations` | Una fila por lote aceptado y snapshot completo | Evidencia inmutable de que el lote estuvo presente en un archivo Copart concreto. | PK: `(snapshot_sha256, lot_key)`; usar para auditoría/timeline de presencia. |
| `copart_auction_attempts` | Una fila por `lot_key` y `auction_at` | Intento de subasta consolidado a partir de una o más observaciones de la misma fecha/hora. | Único: `(lot_key, auction_at)`; fuente principal del timeline. |
| `copart_lot_motivation_signals` | Una fila vigente por `lot_key` | Resumen calculado de re-listados, score y nivel. | PK: `lot_key`; usar en listado, tarjetas y prioridad. |

### `copart_lot_observations`

Cada observación contiene `snapshot_sha256`, `snapshot_downloaded_at`, `lot_key`, `lot_number`, `auction_at`, `current_bid_usd`, `buy_now_usd`, `sale_price_usd`, `lot_status`, `lot_sub_status` y `payload_hash`. La observación no afirma una venta; solo conserva evidencia de presencia y los valores proporcionados por el archivo.

El procesador normal la escribe **después** de que el lote aceptado persiste y solo deriva intentos cuando el snapshot termina completo y sin errores. Por lo tanto, un archivo parcial, corrupto, duplicado o con error no puede generar historia derivada nueva. [1]

### `copart_auction_attempts`

| Campo | Uso de UI | Interpretación |
|---|---|---|
| `attempt_number` | Etiqueta ordinal: “Intento 1”, “Intento 2”. | Numerado por `auction_at` ascendente dentro del lote. |
| `auction_at` | Fecha/hora central del evento. | Un intento equivale a una fecha/hora de subasta distinta. |
| `first_observed_at`, `last_observed_at`, `observation_count` | Opcional en detalle/auditoría. | Rango y cantidad de snapshots que respaldan ese intento. |
| `first_bid_usd`, `last_bid_usd`, `maximum_bid_usd` | Mostrar puja final y opcionalmente rango/máximo. | No son tasación, precio de reserva ni garantía de venta. |
| `buy_now_usd`, `sale_price_usd` | Mostrar únicamente si vienen informados. | `sale_price_usd > 0` es evidencia de `sold_confirmed` en v1. |
| `outcome` | Badge de estado. | Usar las definiciones de la sección 3; no reinterprete. |
| `evidence_level`, `outcome_evidence` | Tooltip/nota de transparencia. | Explica si la conclusión es observada, confirmada o inferida. |

## 3. Resultados de un intento y copy obligatorio

| `outcome` | `evidence_level` habitual | Significado exacto | Badge/copy recomendado |
|---|---|---|---|
| `scheduled` | `source_observed` | El intento todavía no vencía cuando se derivó o no existía evidencia posterior suficiente. | **Programado / pendiente**. “Resultado aún no confirmado.” |
| `sold_confirmed` | `source_confirmed` | El archivo Copart reportó un precio de venta positivo. | **Venta reportada por Copart**. No calificar como oportunidad activa. |
| `relisted_inferred` | `inferred_from_reappearance` | El mismo lote reapareció, tras la fecha anterior, con una fecha de subasta posterior. | **Re-listado inferido**. “El lote reapareció con una nueva fecha; esto no confirma el resultado de la subasta anterior.” |
| `unknown` | `insufficient_evidence` | La fecha pasó sin precio de venta positivo ni reaparición verificable aún. | **Resultado no confirmado**. “No hay evidencia suficiente para concluir venta o no venta.” |

No convierta `unknown` en “no vendido”, no use la ausencia actual del inventario como resultado, y no oculte la nota de evidencia cuando se muestre `relisted_inferred`.

## 4. Señal de oportunidad y score

`copart_lot_motivation_signals` contiene `attempt_count`, `relisted_inferred_count`, `score`, `level`, `first_attempt_at`, `last_attempt_at`, `last_bid_usd`, `historical_maximum_bid_usd` y `score_components` (JSONB). Esta tabla es una **prioridad operativa**, no un pronóstico.

La puerta de seguridad es obligatoria: si no existe por lo menos un `relisted_inferred`, el score es `0` aunque haya antigüedad, varias fechas o variación de pujas. Si se confirma una venta, el score también es `0`. [1]

| Componente acumulativo, solo con evidencia de re-listado | Puntos | Fuente de UI |
|---|---:|---|
| Cada re-listado inferido, máximo 3 | +25 cada uno | `relisted_inferred_count` |
| Tres o más intentos | +20 | `attempt_count` / `score_components.three_or_more_attempts` |
| Primer intento hace 14+ días | +15 | `score_components.first_attempt_at_least_14_days` |
| Última puja inferior al máximo histórico | +15 | `last_bid_usd`, `historical_maximum_bid_usd` y componente JSON |
| Pujas dentro de una variación máxima de 2% | +10 | `score_components.bidding_within_two_percent` |

| `level` | Rango | Presentación recomendada |
|---|---:|---|
| `high` | 60+ | Prioridad alta; conservar la explicación y no usar lenguaje concluyente. |
| `medium` | 35–59 | Señal visible en tarjeta/listado y explicación al abrir. |
| `watch` | 1–34 | Indicador discreto; útil en filtros de asesores. |
| `none` | 0 | No hay señal comercial verificable. No mostrar como oportunidad. |

El JSON `score_components` se debe usar para explicar el badge, no para inventar señales. Sus campos son: `relisted_inferred_count`, `attempt_count`, `relisting_evidence_present`, `sale_confirmed`, `three_or_more_attempts`, `first_attempt_at_least_14_days`, `last_bid_below_historical_maximum`, `bidding_within_two_percent` y `model_version`.

## 5. Diseño recomendado

En el listado, unir la señal por `lot_key` y mostrar solo en lotes que ya cumplen la disponibilidad oficial del portal (`is_active = true` en la capa de inventario). Un lote inactivo no debe volver a hacerse visible por tener historia o score.

| Superficie | Implementación recomendada | No hacer |
|---|---|---|
| Tarjeta/listado | Badge para `medium`/`high`; opcional `watch`. Texto: “Oportunidad por historial”. | Usar “vendedor motivado” como hecho. |
| Filtros de asesores | `level IN ('medium','high')`, con `watch` como filtro opcional. | Filtrar o mostrar por desaparición del lote. |
| Detalle del vehículo | Línea de tiempo ordenada por `auction_at`; puja final/máxima, resultado, nivel de evidencia y disclaimer. | Convertir `unknown` o `relisted_inferred` en una venta/no venta afirmativa. |
| Modal “Cómo se calculó” | Desglosar componentes verdaderos de `score_components` y citar la fecha de última actualización. | Recalcular score en cliente con datos parciales. |

El API agent debe conservar la política de disponibilidad ya existente antes de paginar, contar o devolver facetas. El historial no sustituye el criterio `is_active` ni el control de acceso del API.

### Consulta de referencia para el API agent

La siguiente es una guía de lectura interna para un detalle de un lote. El parámetro debe ser enlazado, no interpolado. La capa del API debe decidir el contrato público, autenticar si corresponde y mantener el filtro de inventario activo fuera de este documento.

```sql
select
  attempts.lot_key,
  attempts.attempt_number,
  attempts.auction_at,
  attempts.first_observed_at,
  attempts.last_observed_at,
  attempts.first_bid_usd,
  attempts.last_bid_usd,
  attempts.maximum_bid_usd,
  attempts.buy_now_usd,
  attempts.sale_price_usd,
  attempts.outcome,
  attempts.evidence_level,
  attempts.outcome_evidence,
  attempts.observation_count,
  signals.attempt_count,
  signals.relisted_inferred_count,
  signals.score,
  signals.level,
  signals.score_components
from copart_auction_attempts attempts
left join copart_lot_motivation_signals signals using (lot_key)
where attempts.lot_key = @lot_key
order by attempts.auction_at asc;
```

## 6. Límite del backfill histórico

El backfill inicial usa `auction_lot_versions` ya preservadas. Esas versiones representan cambios de payload, no una prueba de presencia en cada antiguo archivo Copart. Por eso, una línea de tiempo histórica derivada del backfill tiene menor cobertura que la historia futura; los nuevos snapshots completos guardan una observación por cada lote aceptado presente.

Los resultados verificados el **28 de agosto de 2026** corresponden al primer bloque de hasta 100,000 versiones históricas: 100,000 observaciones sembradas, 61,255 intentos derivados, 625 re-listados inferidos y 0 conversiones fallidas. La distribución fue 33,482 `unknown`, 27,148 `scheduled`, 625 `relisted_inferred` y ninguna venta confirmada dentro de ese bloque. Se generaron 60,531 señales: 56 `watch`, 569 `medium`, 0 `high` y 59,906 `none`. Estos conteos son una foto de ese momento y pueden cambiar con nuevos snapshots completos o nuevos lotes observados.

> No presentes la ausencia de `sold_confirmed` en este backfill como evidencia de que los lotes no fueron vendidos. Simplemente no hubo precio de venta positivo en las versiones procesadas.

## 7. Límites de despliegue y próximos pasos del agente UI/API

Esta tarea actualizó y ejecutó solamente `job-lsc-copart-excel-prod`. No se desplegó `ca-lsc-inventory-api-prod`, no se publicó imagen `api-*`, no se tocó IAAI, Apibara, jobs automáticos, horarios, identidades, secretos ni migraciones generales. La imagen protegida del API se verificó igual antes y después de ambas ejecuciones: `acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:inventory-api-integrated-r20-active-summary`.

| Próximo paso | Responsable | Criterio mínimo |
|---|---|---|
| Diseñar endpoint/bridge de lectura | Agente API/portal | Contrato paginado y protegido; sin VIN/URL/payload crudo; respetar `is_active = true`. |
| Construir cards, filtros y timeline | Agente UI | Copy conservador, evidencia visible y ninguna inferencia por desaparición. |
| Añadir pruebas API/UI | Agente API/portal | `unknown` no aparece como no venta; `relisted_inferred` muestra disclaimer; lote inactivo no reaparece. |
| Desplegar UI/API | Agente API/portal | Bajo su propio plan y validación; fuera de esta tarea de ingesta. |

## Referencias

[1] [Contrato técnico de historial de intentos Copart v1](../inventory-engine/notes/copart_auction_attempt_history_v1.md)
[2] [Límite de despliegue Copart](copart_deployment_boundary.md)
[3] [Handoff general existente para el portal](HANDOFF_PORTAL_AGENT_COPART.md)
