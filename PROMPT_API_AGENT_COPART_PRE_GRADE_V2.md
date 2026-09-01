# Handoff técnico — Copart Pre-Grade v2 con banderas

## Contexto y objetivo

La ingesta Copart adoptará una política de scoring específica: **`lsc_pre_grade_v2`**. El cambio corrige una diferencia de disponibilidad de datos frente a IAAI: el feed Copart actual no entrega un estado afirmativo `Runs & Drives` para la mayoría de los lotes; el valor observado suele ser `DEFAULT` y se normaliza conservadoramente como `UNVERIFIED`.

La política anterior trataba tres banderas de Copart como bloqueos absolutos y devolvía `MANUAL_REVIEW` sin nota numérica. La política v2 no infiere que el vehículo funcione. En cambio, para lotes Copart aceptados, calcula una nota numérica **provisional** y conserva todas las banderas, faltantes, penalidades y nivel de confianza.

## Alcance de la transición

| Plataforma | Política | Comportamiento |
|---|---|---|
| `iaai` | `lsc_pre_grade_v1` | Sin cambio. `MANUAL_REVIEW` y `NEEDS_ENRICHMENT` siguen sin nota numérica. |
| `copart` | `lsc_pre_grade_v2` | Los lotes aceptados reciben `PRE_GRADED` o `PRE_GRADED_WITH_FLAGS`; los descartes/cuarentenas no reciben nota. |

La fuente Copart sigue siendo exclusivamente el snapshot CSV/CSV.GZ descargado en Azure. No hay llamadas a Apibara para Copart.

## Contrato que debe aceptar el API

No recalcules el score ni intentes deducirlo desde datos crudos. Lee el resultado canónico persistido y soporta estos campos existentes:

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

### Semántica obligatoria

| Estado | `preGrade` | Significado y UI |
|---|---:|---|
| `PRE_GRADED` | Número | Pre-grado calculado sin banderas de asesor. Puede participar en ranking normal. |
| `PRE_GRADED_WITH_FLAGS` | Número | Pre-grado provisional con advertencias. Mostrar número, confianza y badges de `reasonCodes`; enviarlo detrás de `PRE_GRADED` en ranking. |
| `MANUAL_REVIEW` | `null` | Estado legado/IAAI de revisión; no fabricar número. |
| `NEEDS_ENRICHMENT` | `null` | Estado legado/IAAI de cobertura insuficiente; no fabricar número. |
| `DISCARDED` | `null` | No tratar como oportunidad comercial ni mezclar con ranking de candidatos. |

Los campos `policyVersion`, `reasonCodes`, `missingFields`, `coveragePercent` y `confidencePercent` son obligatorios para explicar el resultado. El API debe soportar coexistencia v1/v2 durante la transición.

## Reglas de visualización Copart v2

1. Cuando `status = PRE_GRADED_WITH_FLAGS`, no mostrar “sin grading”. Mostrar **“Pre‑grado provisional”**, la nota y la confianza.
2. Mostrar banderas explicables. Ejemplos: `M04` = condición de marcha no confirmada; `M07` = venta sujeta a aprobación/puja mínima; `M02` = documento/título sin equivalencia aprobada.
3. Usar disclaimer junto al score: **“Puntuación preliminar basada en datos declarados por la subasta. No confirma condición mecánica, transferibilidad del título ni resultado de compra.”**
4. No afirmar “Runs & Drives” si `M04` está presente. Tampoco convertir `UNVERIFIED` en negativo absoluto.
5. En ranking: primero `PRE_GRADED`; después `PRE_GRADED_WITH_FLAGS`, con desempate por `preGrade` descendente. Excluir `DISCARDED`; `MANUAL_REVIEW`/`NEEDS_ENRICHMENT` quedan fuera de rankings numéricos.

## Cambio técnico relevante

El hash de entrada incluye la versión de política. Por tanto, cuando v2 llegue a producción, los resultados Copart v1 aparecerán como candidatos de backfill y se recalcularán de forma trazable hacia v2. IAAI no se migrará a v2.

El motor también corrige el reconocimiento de la normalización Copart `RUNS_AND_DRIVES`, otorgándole el factor mecánico completo sin confundirlo con `DriveType`.

## Validación esperada del API

Agregar pruebas de contrato para:

1. `PRE_GRADED_WITH_FLAGS` con `preGrade` numérico y `M04`/`M07` presentes.
2. `MANUAL_REVIEW` con `preGrade = null`.
3. `DISCARDED` fuera de ranking y filtros de oportunidad.
4. Coexistencia de `policyVersion = lsc_pre_grade_v1` y `lsc_pre_grade_v2`.
5. Orden consistente de ranking y paginación calculados sobre lotes `is_active = true` antes de paginar.

## Límites

No cambiar: IAAI, Apibara, elegibilidad D01–D10, D09, fotos/media, Buy Now, reconciliación, cron, secretos, identidades, jobs de scoring globales ni infraestructura. Este handoff no autoriza despliegue. La promoción de imagen Copart y su backfill requieren aprobación operativa separada.
