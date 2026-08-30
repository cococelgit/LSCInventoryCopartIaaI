# Handoff técnico — Taxonomía normalizada de títulos Copart

## Estado y límites

La ingesta Copart ya calcula la taxonomía en el mapper `CopartTitleMapper`, apoyada por `CopartTitleTaxonomy`. Este cambio es **exclusivo de Copart por CSV/CSV.GZ descargado al servidor**. IAAI/Apibara no recibe, no calcula y no debe recibir estos metadatos.

El productor de datos **no creó endpoint, filtro ni faceta pública**. El consumidor API/portal debe leer los campos derivados; no debe repetir ni recrear la clasificación usando texto, regex o reglas de UI.

> La taxonomía es una clasificación operativa de la documentación declarada por Copart. No certifica que un vehículo pueda titularse, registrarse, circular, exportarse o importarse en una jurisdicción particular.

## Commit de producción de datos

Usa el commit que incluya `CopartTitleTaxonomy.cs`, `CopartTitleMapper.cs` y las pruebas de taxonomía. Este handoff se entregará junto al SHA final. Antes de desplegar API, confirma que la imagen de ingesta Copart o el job manual haya procesado la versión `copart-title-taxonomy-v1`.

## Campos canónicos en el payload Copart

Los siguientes campos se guardan como propiedades de extensión de `AuctionVehicle` (nivel raíz del payload JSON), no dentro de `sale_document`. Son derivados y se escriben únicamente para platform `copart`.

| Campo de payload | Tipo | Ejemplo | Significado |
|---|---|---|---|
| `title_category` | `string` | `SALVAGE` | Categoría primaria normalizada. |
| `title_flags` | `string[]` | `["FIRE"]` | Banderas acumulables de divulgación/restricción. |
| `title_review_status` | `string` | `STANDARD` | Ruta operativa de revisión. |
| `title_taxonomy_version` | `string` | `copart-title-taxonomy-v1` | Versión para backfill e idempotencia. |

Los campos fuente existentes se preservan y no se reemplazan:

| Campo fuente preservado | Propósito |
|---|---|
| `source_title_type_code` | Código exacto enviado por Copart, por ejemplo `BS`. |
| `source_title_mapping` | `mapped` o `unmapped`. |
| `source_title_mapping_version` | Versión del catálogo PDF original. |
| `source_title_description_es` | Descripción española del catálogo. |
| `title` y `sale_document.name` | Descripción inglesa fuente mapeada. |
| `title_notes` | Copia auditable de los metadatos; `title_flags` se presenta allí como string separado por `|`, por lo que **el API debe preferir el array del payload raíz**. |

## Contrato público que el API debe exponer

Añade estos campos al modelo público compartido y al tipo TypeScript. Para todo lot no-Copart, devuelve `null` en los strings y `[]` en `titleFlags`; no inventes categorías para IAAI.

```ts
titleCategory: (
  | "CLEAN"
  | "BRANDED_TITLE"
  | "SALVAGE"
  | "REBUILT_RECONSTRUCTED"
  | "NON_REPAIRABLE_PARTS_SCRAP"
  | "EXPORT_ONLY"
  | "DOCUMENT_ONLY"
  | "STATE_VARIANT_VERIFY"
  | "OTHER_UNVERIFIED"
  | null
);
titleFlags: string[];
titleReviewStatus: "STANDARD" | "ADVISOR_REVIEW" | "DOCUMENT_REVIEW" | null;
titleTaxonomyVersion: string | null;
```

Ejemplo de respuesta pública sanitizada:

```json
{
  "lot": "copart:REDACTED",
  "platform": "copart",
  "title": "Salvage Certificate - Fire Damage",
  "titleCode": "BS",
  "titleCategory": "SALVAGE",
  "titleFlags": ["FIRE"],
  "titleReviewStatus": "STANDARD",
  "titleTaxonomyVersion": "copart-title-taxonomy-v1"
}
```

## Categorías primarias

| Código | Etiqueta sugerida | Regla de UI/API |
|---|---|---|
| `CLEAN` | Título limpio | Sin marca documental adicional en el catálogo. |
| `BRANDED_TITLE` | Título con marca | Expone badges de `titleFlags`; no lo presentes como equivalente a título limpio ordinario. |
| `SALVAGE` | Salvage / Salvamento | Categoría principal de títulos salvage; los detalles van en flags. |
| `REBUILT_RECONSTRUCTED` | Reconstruido / Rebuilt | Antecedente de reconstrucción. |
| `NON_REPAIRABLE_PARTS_SCRAP` | No reparable / piezas / chatarra | Mostrar advertencia y ruta de revisión; no llamarlo vehículo de carretera. |
| `EXPORT_ONLY` | Solo exportación | Mostrar solo en flujo adecuado y avisar al asesor. |
| `DOCUMENT_ONLY` | Documento especial | Requiere revisión documental; no equivale automáticamente a un título estándar. |
| `STATE_VARIANT_VERIFY` | Variante estatal — verificar | No reclasificar según palabras del texto; consultar documentación/estado. |
| `OTHER_UNVERIFIED` | Tipo de título por verificar | Código ausente o sin mapa. Mostrar código raw y revisión requerida. |

## Banderas acumulables

El API debe transmitirlas sin convertirlas en una sentencia legal o mecánica: `WATER_FLOOD`, `FIRE`, `STRUCTURAL_FRAME_UNIBODY`, `THEFT`, `ODOMETER`, `LEMON_MANUFACTURER_BUYBACK`, `MECHANICAL`, `DEALER_RESTRICTION`, `TITLE_REVIEW_REQUIRED`, `TITLE_CODE_UNMAPPED`.

No uses `THEFT` como descarte ni como equivalente de daño actual. Es un antecedente documental; muchos lotes pueden tener esta etiqueta. Tampoco derives una bandera a partir de daños, fotos, pujas, llaves o un texto diferente del código de título mapeado.

## Requisitos de consulta y facetas

1. Implementa filtro por `titleCategory` **en PostgreSQL antes de ordenar y paginar**. No filtres resultados en el navegador.
2. Calcula total, páginas y facetas sobre la misma población activa (`is_active = true`) y con el mismo filtro de categoría.
3. Extrae la categoría desde el payload más reciente: `latest.payload ->> 'title_category'`; para banderas usa la matriz JSON `latest.payload -> 'title_flags'`.
4. No recalcules taxonomía a partir de `title`, `titleType`, `sale_document.name` ni `source_title_description_es`.
5. Mantén el filtro actual de títulos especiales hasta que se apruebe una política específica. La nueva categoría no debe cambiar D09 ni convertir una marca en descarte.

## Orden de despliegue y backfill

1. **No desplegado todavía por esta tarea.** Despliega primero el cambio de ingesta mediante el proceso autorizado para `job-lsc-copart-excel-prod`; no alteres IAAI, Apibara, descarga, cron, secretos, identidades ni API en ese paso.
2. Con aprobación explícita, ejecuta el backfill manual existente `--copart-title-backfill`. El selector ahora incluye también los payloads cuya `title_taxonomy_version` no sea `copart-title-taxonomy-v1`; no afecta inventario, lifecycle ni elegibilidad.
3. Verifica en un conjunto agregado que los lotes Copart nuevos tengan versión de taxonomía antes de publicar filtros/facetas de API.
4. Despliega el API/portal bajo su propia frontera de recursos. El API no debe mostrar una categoría como completa en datos anteriores al backfill si el campo aún falta.

## Pruebas que debe añadir el agente de API

- Vehículo Copart `BS` expone `SALVAGE` + `FIRE`.
- Vehículo Copart `AQ` expone `CLEAN` sin flags.
- `B1` expone `STATE_VARIANT_VERIFY` y `DOCUMENT_REVIEW`.
- Código desconocido expone `OTHER_UNVERIFIED`, `TITLE_CODE_UNMAPPED` y `DOCUMENT_REVIEW`.
- IAAI devuelve metadatos de taxonomía nulos/vacíos.
- Un lote inactive no participa en resultados, facetas, total ni paginación al filtrar por categoría.
- Totales y facetas cambian antes de paginación y son consistentes con ficha.

## Prohibiciones

No usar Apibara para Copart. No cambiar D09 ni crear descartes de título. No modificar el código fuente o categoría original. No inferir registrabilidad/transferibilidad. No desplegar un job de Copart ni API sin el flujo de aprobación correspondiente.
