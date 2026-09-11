# Canary V1 versus V2 — 11 de septiembre de 2026

## Resultado ejecutivo

El canary read-only se ejecutó con éxito en Production mediante el workflow de GitHub Actions [34639148005](https://github.com/cococelgit/LSCInventoryCopartIaaI/actions/runs/34639148005), usando el commit `efc33f9`. La ejecución creó un job temporal aislado, no activó el reader público, no cambió schedules y fue eliminado por el propio workflow al terminar.

| Métrica | Resultado |
|---|---:|
| Plataforma solicitada | `all` |
| Filas V2 evaluadas | 513 |
| Filas V2 sin correspondencia en V1 | 0 |
| Mismatches de campos críticos | 0 |
| Mismatches totales | 0 |
| Seller presente sólo en V2 | 108 |
| Seller presente sólo en V1 | 0 |
| Seller conflictivo no nulo | 0 |
| Muestras de conflicto | 0 |

## Campos comparados

El reporte comparó VIN, año, marca, modelo, tipo de vehículo, color, combustible, transmisión, tracción, carrocería, título, daños primario y secundario, seller, estado de subasta, fecha, status, ubicación, facility, odómetro, puja actual, Buy Now, estimaciones, llave, media interna, existencia de fotos, 360, Buy Now activo e indicador `is_active`. Todos reportaron cero diferencias.

## Clasificación

La única diferencia observada está en `seller_name`: 108 lotes tienen seller en V2 donde V1 no lo tenía. No es pérdida ni conflicto; es **enriquecimiento de V2** proveniente de la captura estructurada más completa. No se encontraron casos V1-only ni conflictos entre valores no nulos.

> Decisión: la diferencia de seller no bloquea el canary porque V2 conserva todos los valores V1 y agrega información válida; debe vigilarse en el canary de lectura y en la primera activación pública.

## Alcance y límites

Este resultado valida la paridad de los datos estructurados actuales entre V1 y V2 para una muestra de 513 filas reales. El workflow es read-only. No se ejecutó una purga de V1/JSONB y no se activó `InventoryV2__ReaderEnabled` en Production.

Antes del cutover global todavía corresponde ejecutar una validación funcional de las rutas públicas de búsqueda, resumen, facets y VDP con reader V2 habilitado de forma limitada o interna, incluyendo latencia P50/P95, errores HTTP y filtros combinados. El rollback sigue siendo apagar el flag del reader; la purga histórica debe permanecer bloqueada hasta completar esa validación.

## Conclusión

El canary de datos **pasa el umbral de paridad**: 0 filas faltantes, 0 mismatches críticos y 0 conflictos de seller. El sistema está preparado para el siguiente paso controlado: canary de lectura V2, no todavía para eliminar V1 ni purgar el JSONB histórico.
