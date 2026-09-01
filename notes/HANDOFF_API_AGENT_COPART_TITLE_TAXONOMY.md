# Handoff técnico — Taxonomía canónica de títulos Copart v2

## Estado y límites

La taxonomía de títulos Copart se calcula únicamente por la ingesta CSV/CSV.GZ descargada al servidor. El productor usa la autoridad compartida `TitleFacetCategory`; no existe ni debe recrearse un clasificador Copart paralelo. IAAI/Apibara no recibe ni calcula estos metadatos.

La categoría es una simplificación operativa de un documento declarado por Copart. No certifica titularidad, registro, circulación, exportación ni importación en una jurisdicción. No cambia D09 ni crea descartes automáticos.

## Campos de payload Copart

Después de limpieza y elegibilidad, y solo cuando `LoadToSystem = true`, el payload de Copart contiene los siguientes campos de extensión raíz:

| Campo | Tipo | Significado |
|---|---|---|
| `source_title_raw` | `string \| null` | Código/título fuente conservado para auditoría. |
| `title_category` | `string` | Categoría canónica compacta. |
| `title_flags` | `string[]` | Divulgaciones explicables desde el clasificador canónico. |
| `title_review_status` | `string` | `CLASSIFIED`, `UNVERIFIED` o `REVIEW_REQUIRED`. |
| `title_taxonomy_version` | `string` | `copart-title-taxonomy-v2`. |

Los siguientes datos fuente continúan siendo la evidencia primaria y no se reemplazan: `source_title_type_code`, `source_title_mapping`, `source_title_mapping_version`, `source_title_description_es`, `title`, `sale_document.name` y `title_notes`.

## Contrato público esperado

El API debe leer los campos ya calculados del payload más reciente; no debe recalcularlos mediante texto, regex o reglas de UI. Para IAAI, devolver `null` en los campos string y `[]` en `titleFlags`.

```ts
titleCategory: (
  | "CLEAN"
  | "SALVAGE"
  | "REBUILT"
  | "SPECIAL"
  | "UNVERIFIED"
  | "OTHER"
  | null
);
titleFlags: string[];
titleReviewStatus: "CLASSIFIED" | "UNVERIFIED" | "REVIEW_REQUIRED" | null;
titleTaxonomyVersion: string | null;
```

Ejemplo sanitizado:

```json
{
  "lot": "copart:REDACTED",
  "platform": "copart",
  "title": "Salvage Certificate - Fire Damage",
  "titleCode": "BS",
  "titleCategory": "SALVAGE",
  "titleFlags": ["Salvage"],
  "titleReviewStatus": "CLASSIFIED",
  "titleTaxonomyVersion": "copart-title-taxonomy-v2"
}
```

## Categorías canónicas

| Valor | Uso de UI/API |
|---|---|
| `CLEAN` | Documento identificado como limpio en el mapa canónico; mostrar sus flags, por ejemplo `Theft Recovery`, si los hay. |
| `SALVAGE` | Documento salvage; los detalles permitidos se muestran en `titleFlags`. |
| `REBUILT` | Documento rebuilt/reconstruido. |
| `SPECIAL` | Certificate of Destruction, junk, parts-only, non-repairable u otra documentación especial. Requiere aviso claro. |
| `UNVERIFIED` | Sin fuente suficiente; estado neutral, no equivalencia de clean ni salvage. |
| `OTHER` | Documento dependiente del estado u otro código no apto para simplificación. Mostrar revisión requerida. |

## Consultas y facetas

1. Filtrar `titleCategory` en PostgreSQL, sobre `is_active = true`, **antes** de ordenar, contar y paginar.
2. Obtener los datos desde el payload latest: `payload ->> 'title_category'`, `payload -> 'title_flags'`, `payload ->> 'title_review_status'` y `payload ->> 'title_taxonomy_version'`.
3. No ocultar ni descartar por categoría. `SPECIAL`, `OTHER` y `UNVERIFIED` requieren copy de riesgo y ruta al asesor, no una conclusión legal.
4. No inferir flags a partir de daños, fotos, pujas, llaves o fuentes externas.
5. No tratar una bandera como daño actual ni como garantía documental.

## Orden de despliegue y backfill

La taxonomía v2 requiere primero desplegar la imagen de ingesta Copart bajo su frontera aprobada. Los payloads históricos `v1` quedan detectables por el selector de backfill debido al cambio de versión; el backfill de títulos debe ejecutarse únicamente con aprobación separada. El API/portal no debe declarar cobertura total hasta verificar la versión v2 en los lotes requeridos.

## Pruebas mínimas para API/portal

- `AQ` expone `CLEAN` y `CLASSIFIED`.
- `BS` expone `SALVAGE` y conserva el título/documento fuente.
- `AR` expone `REBUILT`.
- `AD` expone `SPECIAL`.
- `B1` expone `OTHER` y `REVIEW_REQUIRED`.
- Código desconocido expone `OTHER` y `REVIEW_REQUIRED`; fuente ausente expone `UNVERIFIED`.
- IAAI recibe campos nulos/vacíos.
- Un lote inactivo no participa en facetas, total, páginas ni resultados.

## Prohibiciones

No usar Apibara para Copart. No recalcular títulos en navegador. No cambiar D09. No crear descartes de título. No sustituir el código/descripción fuente. No desplegar jobs o API sin el proceso de aprobación correspondiente.
