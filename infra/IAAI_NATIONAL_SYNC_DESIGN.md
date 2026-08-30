# Diseño operativo — IAAI nacional y refresco recurrente

## Objetivo

Construir el inventario nacional **IAAI** en PostgreSQL sin utilizar Apibara para Copart, y mantenerlo actualizado mediante tandas recurrentes de treinta minutos. Copart seguirá entrando únicamente por el adaptador Excel.

## Decisiones técnicas

| Componente | Decisión | Control |
|---|---|---|
| Carga inicial | Paginación nacional IAAI por cursor, en tandas de tamaño limitado | Cada tanda persiste el cursor antes de terminar; una reanudación no reinicia la carga desde el primer lote. |
| Actualización recurrente | Recorrido **rotativo**, no reescaneo completo | La API pública documenta cursores, pero no documenta un filtro `updated_since`; no se afirmará una sincronización delta real sin ese contrato. |
| Cuota | Medición de uso antes/después y presupuesto máximo de solicitudes por tanda | El job se detiene al agotar su presupuesto configurado; un 429 queda registrado y no habilita reintentos en cascada. |
| Enriquecimiento | Presupuesto independiente de consultas de detalle | Las listas se persisten siempre que pasen elegibilidad; los detalles se enriquecen solo hasta el límite configurado. |
| Concurrencia | Lease distribuido en PostgreSQL por flujo | Una segunda ejecución se marca como omitida si existe una lease vigente. |
| Reconciliación | Solo al completar un ciclo nacional íntegro | Las observaciones de cada ciclo se almacenan y se aplican a lifecycle al final; nunca se despublica por una tanda parcial. |
| Consulta UI | Paginación/filtrado desde PostgreSQL, no solo los 1,000 más recientes | La carga nacional no se declarará operativa para búsqueda completa hasta sustituir el límite actual de lectura. |

## Límites iniciales propuestos

| Parámetro | Valor inicial | Motivo |
|---|---:|---|
| Página Apibara | 20 | Máximo documentado por el contrato. |
| Páginas por tanda | 50 | Hasta 1,000 listados por ejecución. |
| Consultas máximas por tanda | 80 | Deja margen para detalle, usage y fallos controlados. |
| Detalles por tanda | 20 | Mantiene los campos ampliados sin convertir cada refresco en 1,000 consultas extra. |
| Frecuencia | 30 min | Coincide con el ciclo general de actualización comunicado por el proveedor; es un recorrido rotativo local. |
| Timeout de réplica | 30 min | Una tanda no debe cruzarse con la siguiente; la lease expira ligeramente después de ese límite. |
| Reintento Azure | 0 | Los reintentos automáticos pueden duplicar consultas; el proceso es idempotente y se reanuda en el siguiente intervalo. |

## Secuencia de activación

1. Medir una vez el uso/plan vigente de Apibara y confirmar que la capacidad mensual soporta las tandas previstas.
2. Desplegar el procesador nacional y la consulta paginada de PostgreSQL.
3. Ejecutar una tanda nacional controlada y verificar cursor, lease, uso y persistencia.
4. Activar el job de treinta minutos con IAAI exclusivamente, concurrencia uno y límites anteriores.
5. Supervisar dos ciclos completos antes de ampliar detalles o volumen.

## Límites explícitos

La API pública de Apibara documenta paginación por cursor y menciona flujos incrementales, pero el contrato revisado no expone un parámetro `updated_since`. Por ello, los refrescos de treinta minutos se describen como **recorrido rotativo controlado** hasta que el proveedor confirme un endpoint delta o feed de cambios. Esta decisión evita prometer que los 60,000+ lotes se revalidan íntegramente cada treinta minutos.

## Fuentes externas verificadas

1. [Apibara Vehicle Auction Data Infrastructure — cursor pagination, checkpoints e intervalo general aproximado de 30 minutos](https://apibara.tech/en)
2. [Apibara Vehicle Auction API OpenAPI — `/vehicles`, cursor y máximo `per_page=20`](https://apibara.tech/openapi/vehicle-auction-data-api.json)
