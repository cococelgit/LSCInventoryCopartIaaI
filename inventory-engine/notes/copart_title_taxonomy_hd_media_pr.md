# Copart: taxonomía canónica persistida y enriquecimiento HD controlado

**Estado:** cambio de código y pruebas preparado para PR. Este documento no autoriza despliegues, ejecuciones manuales, cambios de cron ni cambios de infraestructura.

## Alcance y límites

La taxonomía se aplica exclusivamente a lotes **Copart** recibidos desde el CSV/CSV.GZ descargado al servidor. IAAI continúa exclusivamente por Apibara/Open y no recibe campos, clasificación, enriquecimiento ni backfill de esta implementación.

El cambio no altera la elegibilidad D00A–D10, no reactiva D09, no consulta Apibara para Copart y no modifica la reconciliación. Las fotos HD son un enriquecimiento posterior y explícito: el procesador normal de snapshots no hace solicitudes HTTP a Copart.

## Fuente canónica de clasificación

El PR incorpora sin reinterpretar la autoridad `TitleFacetCategory` desde el commit canónico `e4d2dc8` de la rama de títulos. No permanece `CopartTitleTaxonomy`; por tanto, no hay un segundo clasificador Copart.

Después de `CanonicalVehicleCleaner` y `AuctionEligibilityEvaluator`, y solo cuando `LoadToSystem = true`, `CopartTitleMapper.ApplyTaxonomy` añade los siguientes metadatos al payload del snapshot:

| Campo | Semántica |
|---|---|
| `source_title_raw` | Código/título original de Copart, preservado para auditoría. |
| `title_category` | Una de `CLEAN`, `SALVAGE`, `REBUILT`, `SPECIAL`, `UNVERIFIED` u `OTHER`. |
| `title_flags` | Banderas explicables del clasificador canónico, por ejemplo `Salvage`, `Rebuilt`, `Theft Recovery`, `Flood` o `Document review`. |
| `title_review_status` | `CLASSIFIED`, `UNVERIFIED` o `REVIEW_REQUIRED`. |
| `title_taxonomy_version` | `copart-title-taxonomy-v1`. |

El código y la descripción originales continúan en `source_title_type_code`, `sale_title_type_code`, `title`, `sale_document` y `title_notes`. La categoría simplificada nunca sustituye el documento fuente ni hace afirmaciones de registrabilidad.

Los lotes descartados o cuarentenados **no** reciben taxonomía. Los vehículos `MARCAR` que se cargan sí se clasifican. Las mismas entradas generan el mismo payload y hash; un snapshot posterior no debe crear una nueva versión de título si el código y los metadatos canónicos no cambiaron.

El backfill manual de títulos existente reutiliza exactamente el mismo mapper y clasificador. No debe ejecutarse como parte de este PR sin una autorización separada.

## Fotos HD y galería

`CopartMediaEnrichmentProcessor` selecciona solo lotes Copart ya persistidos y activos con cero o una imagen. Está aislado de elegibilidad, scoring, reconciliación, lifecycle, título e IAAI.

Por cada secuencia de la respuesta de catálogo Copart, `CopartMediaResolver` conserva una sola imagen con esta prioridad:

```text
HD → estándar → miniatura
```

Las secuencias se ordenan de forma estable, se eliminan duplicados y se guardan solo enlaces HTTPS aprobados de `copart.com`. Los endpoints de catálogo, query values y URLs privadas continúan fuera del contrato público: el navegador debe usar el proxy first-party existente.

Antes de sustituir la galería, el worker guarda `copart_media_original_photos` en el payload privado. El `Image URL` fuente se mantiene en `raw_source` para auditoría. Una galería con más de una imagen deja de ser candidata; un 404, URL inválida o fallo transitorio permanece controlado y reintentable, sin modificar la decisión de elegibilidad ni el resultado de scoring.

Los códigos operativos sanitizados son `NOT_FOUND_404`, `INVALID_URL`, `MISSING_CATALOG_URL`, `INVALID_CATALOG_RESPONSE`, `INCOMPLETE_GALLERY`, `HTTP_<status>` y `REQUEST_<exception>`.

## Métricas

El resultado runtime de una ingesta Copart incorpora `TitleTaxonomy` con:

```json
{
  "classified": 0,
  "unverified": 0,
  "reviewRequired": 0,
  "categoryCounts": {
    "CLEAN": 0,
    "SALVAGE": 0,
    "REBUILT": 0,
    "SPECIAL": 0,
    "UNVERIFIED": 0,
    "OTHER": 0
  }
}
```

El resultado del enriquecimiento de media incorpora `Metrics` con candidatos, galerías resueltas, filas ya completas, fallos, galerías, imágenes HD, galerías solo miniatura, 404, URLs inválidas y duración/p50/p95 de resolución.

> Para respetar el límite de este PR, no se agregan columnas, tablas, índices ni migraciones al Centro de Ejecuciones. Las métricas se emiten por el resultado/log estructurado del worker; el detalle histórico anterior permanece como `N/D`. Su persistencia visual en el Centro de Ejecuciones requiere un cambio de contrato/metadata aprobado en una tarea independiente.

## Validación requerida antes de cualquier despliegue

```bash
dotnet test inventory-engine/Lsc.Inventory.sln -c Release
pnpm check
```

Las pruebas cubren clasificación canónica, fuente raw, desconocidos, IAAI sin cambios, taxonomía solo posterior a elegibilidad, idempotencia, galería HD, orden por secuencia, galería ya completa, 404, URL inválida, fallo transitorio, preservación de referencias originales y aislamiento de media respecto a score/elegibilidad.

## Operación posterior aprobable

No se debe ejecutar `--copart-media-enrich`, backfill de títulos, ni actualizar imágenes/Jobs dentro de este PR. Si se aprueba una operación posterior, el enriquecimiento debe invocarse manualmente, por lote y con concurrencia limitada ya configurada, verificando métricas agregadas y manteniendo la carga de snapshots independiente.
