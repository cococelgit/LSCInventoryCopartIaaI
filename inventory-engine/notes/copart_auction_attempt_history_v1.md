# Historial de intentos de subasta Copart v1

## Objetivo

Construir una línea de tiempo por lote Copart a partir de snapshots completos de Excel, con evidencia explícita de fecha de subasta, puja, Buy Now, estado y presencia posterior. El sistema debe identificar re-listados probables sin afirmar una venta o una decisión del vendedor que el feed no confirme.

## Unidad de evidencia: observación

Una **observación** corresponde a un lote presente en un snapshot Copart completo y aceptado. Se identifica de forma inmutable por `(snapshot_sha256, lot_key)` y conserva el momento de descarga, fecha/hora de subasta, puja actual, Buy Now, precio de venta reportado, estado/subestado y hash de payload. La observación no se crea para archivos parciales, corruptos, duplicados o corridas con errores.

## Unidad comercial: intento de subasta

Un **intento** agrupa observaciones del mismo `lot_key` que comparten la misma fecha/hora de subasta normalizada. Si el lote es observado varias veces antes de la misma hora de subasta, se actualizan la primera/última vez vista y el máximo/último valor de puja; no se cuenta como un segundo intento.

| Resultado | Regla de evidencia | Confianza |
|---|---|---|
| `scheduled` | La fecha/hora de subasta aún no vence y el lote está presente. | Alta |
| `sold_confirmed` | Copart reporta un precio de venta positivo en el listado. | Alta |
| `relisted_inferred` | Un intento anterior venció y el mismo lote reaparece en un snapshot completo posterior con una fecha de subasta posterior. | Media-alta |
| `unknown` | El lote deja de verse, no hay resultado explícito de venta y no hay reaparición posterior verificable. | Baja/indeterminada |

No se clasifica una desaparición como no venta. Tampoco se usa el texto de título, daño, vendedor o precio como sustituto de un resultado de subasta.

## Señal de oportunidad comercial

La señal es una priorización de lotes, no una afirmación de que el vendedor esté obligado a vender.

| Condición acumulada | Puntos |
|---|---:|
| Cada re-listado inferido posterior al primero, hasta tres | +25 |
| Tres o más intentos totales | +20 |
| Catorce o más días desde el primer intento | +15 |
| Última puja menor que la puja máxima histórica | +15 |
| Pujas estancadas (variación máxima <= 2%) entre intentos | +10 |
| Venta confirmada o intento pendiente no vencido | 0; no se etiqueta como oportunidad por sí solo |

Clasificación: `high` para 60+, `medium` para 35–59, `watch` para 1–34 y `none` para 0. La salida debe incluir los componentes que generaron el score.

## Aislamiento y seguridad

La funcionalidad aplica únicamente a `platform = copart` y a snapshots Excel completos. No invoca Apibara, no modifica elegibilidad, reconciliación, lifecycle, títulos, media, IAAI ni el API público. Los registros conservan `snapshot_sha256` para trazabilidad y no agregan VINs completos a auditorías o handoffs.

## Uso futuro en UI

El UI deberá leer una vista/consulta interna derivada para mostrar: cantidad de intentos, fecha y puja por intento, re-listados inferidos, score, nivel y razones. El frontend nunca debe afirmar “vendedor obligado a vender”; debe usar el texto **“señal de oportunidad basada en historial de subasta”** y revelar si el resultado es inferido o confirmado.
