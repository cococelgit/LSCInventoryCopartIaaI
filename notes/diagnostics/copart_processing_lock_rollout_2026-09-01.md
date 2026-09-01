# Copart processing lock rollout — 2026-09-01

## Fuente de evidencia

Diagnósticos Azure de solo lectura ejecutados mediante GitHub Actions y workflows de promoción aislada.

## Estado previo

- Dos ejecuciones automáticas Copart que se solapaban fueron detenidas con autorización del usuario.
- El cron permaneció en `0,30 * * * *`.
- La imagen protegida del API no cambió durante la promoción: `acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:inventory-api-integrated-r70-buy-now-public-contract`.

## Cambio promovido

- Commit de candado PostgreSQL Copart: `99ca265`.
- Commit que trata snapshot duplicado/candado ocupado como no-op exitoso: `c13e853`.
- Imagen automática Copart antes: `acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:copart-cdbac2d0c0f3add67e5a7634f8ca629bbad90c08`.
- Imagen automática Copart después: `acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:copart-c13e853d666f422112ff6511c1b9df06a3443aca`.
- El workflow verificó que la única configuración modificada fue la imagen; cron, argumentos, identidad, configuración y API permanecieron sin cambios.

## Observación transitoria posterior a la promoción

El diagnóstico a las 18:34 UTC encontró:

| Ejecución | Inicio UTC | Estado al diagnóstico | Interpretación |
|---|---:|---|---|
| `job-lsc-copart-auto-prod-29804760` | 18:00 | Running | Inició antes de que se promoviera la imagen con candado (promoción a las 18:11 UTC), por lo que no llevaba el lock. |
| `job-lsc-copart-auto-prod-29804790` | 18:30 | Running | Primera ejecución programada con la nueva imagen. Puede adquirir el lock porque la ejecución de las 18:00 no lo tenía. |

Este solapamiento específico fue transitorio de rollout. La verificación funcional se realizó en la ventana programada siguiente, a las 19:00 UTC.

## Verificación programada de producción — 19:00 UTC

El diagnóstico de solo lectura posterior a la ventana de las 19:00 UTC (workflow `33547047720`) confirmó el comportamiento esperado:

| Ejecución | Inicio UTC | Fin UTC | Estado | Lectura |
|---|---:|---:|---|---|
| `job-lsc-copart-auto-prod-29804790` | 18:30:00 | — | Running al diagnóstico | Ejecución que tenía la nueva imagen y mantenía el lease. |
| `job-lsc-copart-auto-prod-29804820` | 19:00:00 | 19:00:37 | Succeeded | Terminó en 37 segundos mientras la de 18:30 seguía activa. Esto coincide con el no-op exitoso por lease ocupado. |

Los logs de réplica de Azure ya no estaban disponibles al momento del diagnóstico, por lo que no se recuperó literalmente la línea `SKIPPED_LOCK_HELD`. Sin embargo, la combinación de la ejecución de 18:30 todavía activa, la ejecución de 19:00 exitosa de 37 segundos y la imagen `c13e853` —que ejecuta la ruta de lock antes de abrir el snapshot— es evidencia operativa consistente con la contención esperada. No se debe interpretar como una auditoría de filas, porque los logs efímeros no permitieron consultar el sync-run o los conteos de esa invocación.

No se iniciaron ejecuciones manuales para esta observación.

## Invariantes verificados después de la observación

| Invariante | Valor observado |
|---|---|
| Job automático Copart | `job-lsc-copart-auto-prod` |
| Imagen automática | `acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:copart-c13e853d666f422112ff6511c1b9df06a3443aca` |
| Cron | `0,30 * * * *` |
| Parallelism | `1` |
| Retry limit | `0` |
| Replica timeout | `3600` segundos |
| Imagen API protegida | `acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:inventory-api-integrated-r70-buy-now-public-contract` |

No se modificaron IAAI, el API, los demás jobs, secretos, identidades, fuente de Copart ni se iniciaron ejecuciones manuales del job automático durante la verificación.

## Conclusión

El rollout queda validado a tres niveles diferenciados: pruebas unitarias de semántica del lease; promoción de imagen aislada; y una observación programada de producción consistente con la ejecución competidora cerrando como no-op exitoso. No se afirma visibilidad de `SKIPPED_LOCK_HELD` en logs de Azure porque el sistema ya había depurado la réplica y sus logs; se conserva esa limitación explícita.
