# Inventory V2 — Fase 3: reader canary en Production

El API productivo `ca-lsc-inventory-api-prod` tiene activa la revisión `ca-lsc-inventory-api-prod--v2r1` con **5% del tráfico**. La revisión anterior `ca-lsc-inventory-api-prod--po1` conserva **95%** del tráfico.

| Control | Resultado |
|---|---|
| `InventoryV2__ReaderEnabled` | `true` en la revisión canary |
| `InventoryV2__LegacyReadFallbackEnabled` | `true` |
| Estado reader en PostgreSQL | Activado junto con writer |
| Health checks | 5 respuestas exitosas consecutivas |
| Tráfico V2 | 5% |
| Tráfico legacy | 95% |
| Purga V1/JSONB | No ejecutada |
| Schedules | No modificados |

La verificación read-only fue completada por el workflow [34649354426](https://github.com/cococelgit/LSCInventoryCopartIaaI/actions/runs/34649354426). El primer workflow de activación terminó con fallo únicamente porque no persistía `NEW_REVISION` entre pasos; el estado productivo resultante fue comprobado posteriormente y coincide con el canary esperado. El workflow quedó corregido para futuros despliegues.

La ventana de observación debe mantenerse durante 24–48 horas. Durante ese período se deben revisar latencia P95, errores HTTP, resultados vacíos, facets, Buy Now, VDP, media y logs del API. No se debe subir el tráfico a 100%, apagar el fallback ni borrar JSONB hasta completar esa observación.
