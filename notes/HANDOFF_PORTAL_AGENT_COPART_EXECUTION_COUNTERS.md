# Handoff — contadores de altas, actualizaciones y sin cambio en ejecuciones Copart

## Objetivo

Mostrar en el Centro de Ejecuciones los contadores reales de persistencia de cada snapshot Copart completo:

| Campo público interno | Semántica |
|---|---|
| `created` | Lotes Copart cuyo `lot_key` no existía antes de esta corrida. |
| `updated` | Lotes existentes con un payload Copart materialmente diferente. |
| `unchanged` | Lotes existentes con el mismo payload ya versionado. |

Estos contadores no son métricas de grading. Son el resultado de comparar el lote y `payload_hash` antes de persistir cada vehículo.

## Backend de ingesta

Los contadores ya se persisten al finalizar cada snapshot Copart completo en `copart_snapshot_manifests`:

- `created_count integer nullable`
- `updated_count integer nullable`
- `unchanged_count integer nullable`

El manifiesto se relaciona con `inventory_sync_runs` mediante `run_id`. Esta es la fuente correcta tanto para ejecuciones nuevas como históricas. La ingesta no debe escribir columnas nuevas en `inventory_sync_runs`, porque el job productivo ejecuta con migraciones generales desactivadas.

IAAI no usa el manifiesto Copart ni debe recibir valores derivados de esta unión.

## Consulta recomendada para el router de auditoría

Para registros `provider = 'copart-excel'`, hacer un `LEFT JOIN copart_snapshot_manifests manifest ON manifest.run_id = run.run_id` y proyectar:

```sql
case when run.provider = 'copart-excel'
          and run.state_scope = 'all'
          and manifest.is_complete = true
          and manifest.status = 'succeeded'
     then manifest.created_count
     else null
end as created,
case when run.provider = 'copart-excel'
          and run.state_scope = 'all'
          and manifest.is_complete = true
          and manifest.status = 'succeeded'
     then manifest.updated_count
     else null
end as updated,
case when run.provider = 'copart-excel'
          and run.state_scope = 'all'
          and manifest.is_complete = true
          and manifest.status = 'succeeded'
     then manifest.unchanged_count
     else null
end as unchanged
```

No convertir `NULL` en cero. La UI debe representar `NULL` como `N/D` o “No aplica”.

## Casos obligatorios de UI

| `state_scope` / situación | `created` / `updated` / `unchanged` |
|---|---|
| `all` + snapshot Copart completo exitoso | Mostrar los tres valores reales. |
| `duplicate` | `N/D`; se validó el archivo pero no hubo comparación por lote ni persistencia. |
| `skipped_lock_held` | `N/D`; no se abrió archivo, no se persistió y no se reconcilió. |
| Validación fallida o snapshot incompleto | `N/D`; los conteos parciales no son auditables como resultado final. |
| IAAI | Mantener su contrato y lógica existente; no inferir valores Copart. |

## Límites

- No modificar endpoints públicos de inventario.
- No cambiar filtros, lifecycle, elegibilidad, scoring, Buy Now, media, cron, fuentes ni IAAI.
- No hacer backfill que invente cambios de vehículos. El `LEFT JOIN` permite mostrar los conteos de manifiestos históricos que sí fueron guardados.
