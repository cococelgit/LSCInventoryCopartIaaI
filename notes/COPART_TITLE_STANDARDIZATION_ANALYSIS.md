# Estandarización de títulos Copart — propuesta v1

## Decisión recomendada

**Sí: debemos agrupar los títulos de Copart en menos de diez categorías, pero no reemplazar el título original.** El resultado correcto es un modelo de tres capas: el código y descripción originales para trazabilidad, una categoría principal sencilla para búsqueda/UX, y banderas separadas para riesgos o restricciones específicas.

No recomiendo un único campo que diga solo `SALVAGE` o `CLEAN`. Eso reduciría la fricción en la interfaz, pero escondería diferencias críticas como **inundación**, **no reparable**, **export only**, **reconstruido** o **documento sin título**. La agrupación debe facilitar la selección sin convertirse en una afirmación legal de que el vehículo puede titularse o circular en un estado concreto.

> La categoría es una clasificación operativa y de experiencia de cliente. El código de Copart, la descripción fuente y la documentación real siguen siendo la fuente de verdad para validar transferencia, titulación, exportación y registro.

## Base analizada

El catálogo vigente contiene **181 códigos** provenientes del PDF de títulos aportado. Se analizó también el archivo Copart local `salesdata.csv`: **146,248 filas**, **198 códigos fuente distintos**, de los cuales **164** están en el catálogo y **34** no aparecen mapeados. La cobertura por filas es **145,864 mapeadas (99.73%)** y **384 sin mapa (0.26%)**. [1] [2]

| Métrica | Resultado |
|---|---:|
| Códigos del catálogo aprobado | 181 |
| Códigos distintos del snapshot | 198 |
| Códigos mapeados presentes | 164 |
| Códigos fuente sin mapa | 34 |
| Filas analizadas | 146,248 |
| Filas con código mapeado | 145,864 (99.73%) |
| Filas con código desconocido o ausente | 384 (0.26%) |

## Taxonomía propuesta — nueve categorías

La siguiente tabla usa una categoría primaria única. Las variantes y riesgos se guardan como banderas adicionales; por ejemplo, un título salvage por inundación queda `SALVAGE` con la bandera `WATER_FLOOD`, no como una categoría número diez.

| Código de categoría | Etiqueta visible sugerida | Cuándo aplica | Filas del snapshot | Política comercial sugerida |
|---|---|---|---:|---|
| `CLEAN` | Título limpio | Clear/Clean/Certificate of Clean Title sin marca adicional | 101 (0.07%) | Mostrar normal; nunca prometer que está libre de daños o gravámenes. |
| `CLEAN_BRANDED` | Título limpio con marca | Clean/Clear con marca explícita, por ejemplo water, theft recovery, fleet o rebuildable | 27,367 (18.71%) | Mostrar la marca como alerta obligatoria; no presentarlo como equivalente a título limpio ordinario. |
| `SALVAGE` | Salvage / Salvamento | Salvage Title o Salvage Certificate reparable, genérico o con causa declarada | 109,216 (74.67%) | Categoría principal de inventario de subasta; divulgar toda bandera aplicable. |
| `REBUILT_RECONSTRUCTED` | Reconstruido / Rebuilt | Rebuilt, reconstructed, repaired, restored u overhauled | 6,143 (4.20%) | Divulgar que el documento trae antecedente de reconstrucción; no inferir condición mecánica actual. |
| `NON_REPAIRABLE_PARTS_SCRAP` | No reparable / piezas / chatarra | Certificate of Destruction, Non-repairable, Parts Only, Junk, Scrap o Crushed | 2,926 (2.00%) | No comercializar como vehículo de uso en carretera. Requiere revisión manual antes de cualquier proceso de compra/exportación. |
| `EXPORT_ONLY` | Solo exportación | Export Only | 5 (<0.01%) | Mostrar solo en flujos de exportación y confirmar destino/documentos con el cliente. |
| `DOCUMENT_ONLY` | Documento especial | Bill of Sale, Certificate of Origin, Ownership, Liability o Emissions | 24 (0.02%) | No asumir que equivale a título estándar; revisión documental/manual obligatoria. |
| `STATE_VARIANT_VERIFY` | Variante estatal — verificar | B1, B2, B3, C1, C4, D1 o D2; el PDF indica que su significado final depende del estado | 82 (0.06%) | No asignar automáticamente a Salvage/Rebuilt/Destruction; revisión por estado antes de cotizar como titulable. |
| `OTHER_UNVERIFIED` | Tipo de título por verificar | Código ausente o no mapeado | 384 (0.26%) | Mantener el código raw; enviar a cola de mapeo. No inventar significado ni elegibilidad. |

## Qué se agrupa y qué debe permanecer separado

Agrupar los más de cien subtipos de salvage dentro de `SALVAGE` es correcto para filtros y tarjetas. Esos subtipos describen a menudo la causa o el antecedente, no una categoría comercial diferente. Sin embargo, **la causa no se debe perder**: water/flood, fire, structural/frame/unibody, theft/recovery, odometer, lemon/buyback y mechanical deben continuar como banderas separadas.

| Bandera secundaria propuesta | Ejemplos de descripciones fuente | Uso correcto |
|---|---|---|
| `WATER_FLOOD` | Water, Flood | Divulgación visible; no deducir condición actual del vehículo. |
| `FIRE` | Fire | Divulgación visible y prioritaria. |
| `STRUCTURAL_FRAME_UNIBODY` | Structural, Frame, Unibody | Divulgación visible y prioritaria. |
| `THEFT` | Theft, Stolen, Theft Recovery | Antecedente documental; no equivale necesariamente a robo activo ni a daño actual. |
| `ODOMETER` | Not Actual Mileage, Odometer Tampered | Divulgación visible y revisión adicional. |
| `LEMON_MANUFACTURER_BUYBACK` | Lemon, Manufacturer Buyback | Divulgación visible y explicación específica. |
| `MECHANICAL` | Mechanical, Engine, Transmission | Divulgación visible; no sustituye inspección. |
| `DEALER_RESTRICTION` | Dealer Only | Control de acceso/asesor antes de puja. |
| `STATE_VERIFICATION_REQUIRED` | Variant, Uncertain, Out of State | Cola manual; no clasificar por suposición. |

En el snapshot analizado, la bandera documental `THEFT` aparece en 110,191 filas (75.34%), principalmente por los códigos `SC`, `ST` y `CT`. Por su volumen, **no debe tratarse como un filtro de descarte ni como una alerta de daño equivalente a incendio o inundación**. Debe mostrarse como un antecedente de documento, con lenguaje cuidadoso y sin afirmar hechos adicionales sobre el vehículo.

## Modelo de datos recomendado

Agregar campos derivados, sin sobrescribir los existentes. El código fuente y las descripciones actuales deben conservarse indefinidamente.

| Campo recomendado | Ejemplo | Objetivo |
|---|---|---|
| `title_source_code` | `BS` | Código Copart exacto, ya disponible. |
| `title_source_description_en` | `Salvage Certificate - Fire Damage` | Evidencia fuente, ya disponible. |
| `title_source_description_es` | `Certificado de Salvamento - Daño por Fuego` | Etiqueta en español, ya disponible. |
| `title_mapping_status` | `mapped` / `unmapped` | Auditoría del catálogo. |
| `title_category` | `SALVAGE` | Filtro y etiqueta principal de máximo nueve opciones. |
| `title_flags` | `["FIRE"]` | Divulgaciones o restricciones acumulables. |
| `title_review_status` | `STANDARD`, `ADVISOR_REVIEW`, `DOCUMENT_REVIEW` | Workflow interno; no sustituye validación legal. |
| `title_taxonomy_version` | `copart-title-category-v1` | Reproducibilidad y backfill seguro. |

## Reglas de implementación seguras

Primero, construir una tabla de mapeo explícita por código Copart, versionada y testeada. No usar fuzzy matching de texto para códigos nuevos. Un código desconocido cae siempre en `OTHER_UNVERIFIED` y genera una tarea de revisión, conservando el valor original.

Segundo, mantener `D09` desactivada como está hoy. La categoría no debe crear descartes automáticos de título. `NON_REPAIRABLE_PARTS_SCRAP`, `DOCUMENT_ONLY`, `STATE_VARIANT_VERIFY`, `EXPORT_ONLY` y `OTHER_UNVERIFIED` pueden generar una marca y una ruta de revisión, pero un cambio de elegibilidad requiere aprobación separada y pruebas de negocio/legales.

Tercero, resolver conflictos por severidad con una precedencia explícita. Por ejemplo, `Clean Title - Non-Repairable` debe ser `NON_REPAIRABLE_PARTS_SCRAP`, no `CLEAN`; `Clean Title - Rebuilt` debe ser `REBUILT_RECONSTRUCTED`; y B1/C1/D1 deben permanecer `STATE_VARIANT_VERIFY`, aunque su descripción contenga palabras como salvage o rebuilt.

## Implementación recomendada por fases

| Fase | Cambio | Riesgo | Resultado |
|---|---|---|---|
| 1. Modelo | Añadir los cuatro campos derivados y el catálogo v1, sin cambiar pantalla, filtros ni elegibilidad | Bajo | Datos consistentes y reversibles. |
| 2. Backfill | Clasificar inventario Copart existente usando código fuente ya preservado; registrar `title_taxonomy_version` | Medio | Inventario actual listo para filtrar. |
| 3. Asesor/UI | Filtro por `title_category`, badges por `title_flags`, y aviso de revisión | Bajo | Menos confusión sin ocultar información material. |
| 4. Política | Decidir qué categorías se muestran por defecto y qué rutas necesitan asesor | Medio | Reglas comerciales explícitas, no inferencias técnicas. |

**Mi recomendación concreta:** implementar las nueve categorías anteriores, habilitar filtros por `title_category`, y mostrar las banderas en la tarjeta/ficha. El selector por defecto debe incluir `CLEAN`, `CLEAN_BRANDED`, `SALVAGE` y `REBUILT_RECONSTRUCTED`; las cinco categorías restantes deben poder verse, pero con aviso y ruta de revisión. No bloquear ni eliminar por categoría hasta que se defina una política comercial separada.

## Archivos de soporte

La matriz adjunta contiene la propuesta por cada código del catálogo y cada código observado en el snapshot: descripción, categoría, banderas, recomendación fuente, frecuencia y estado de mapeo. Es una base de decisión, no un cambio productivo.

## Referencias

[1]: ../../upload/Títulos.pdf "Documento de mapeo de títulos Copart aportado por el usuario"
[2]: ../../upload/salesdata.csv "Snapshot Copart local analizado; 146,248 filas"
