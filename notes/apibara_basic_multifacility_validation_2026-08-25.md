# Validación Apibara Basic y sincronización multi-facility — 2026-08-25

## Plan y cuota observados

El endpoint protegido `/api/v1/usage` reportó el plan **Basic**, límite de **30,000** solicitudes, **92** utilizadas y **29,908** restantes al momento de la consulta. El límite por página reportado fue de **20**.

## Facilities Copart Florida observadas

La consulta oficial de locations de Apibara devolvió 14 facilities para Florida:

| Facility | Ciudad |
|---:|---|
| 366 | Clewiston |
| 86 | Fort Pierce |
| 163 | Jacksonville |
| 105 | Miami |
| 33 | Opa Locka |
| 148 | Homestead |
| 108 | Ocala |
| 153 | Apopka |
| 55 | Orlando |
| 348 | Arcadia |
| 117 | Midway |
| 335 | Thonotosassa |
| 34 | Riverview |
| 70 | West Palm Beach |

## Primera corrida con elegibilidad v3

La ejecución Azure `job-lsc-inventory-scheduled-prod-q1rf1m0` terminó en estado **Succeeded**. El resumen del contenedor reportó **14 scopes**, **280 vehículos observados**, **35 solicitudes** y **0 fallos**. Los logs confirmaron descartes efectivos antes del upsert por reglas como D03, D05 y D10.

Después del despliegue y las validaciones, Apibara reportó **218/30,000** solicitudes utilizadas y **29,782** restantes, sin errores dentro del período de la suscripción. El dominio publicado devolvió **487 lotes elegibles**, cobertura de las **14 facilities**, **52 títulos especiales cargados**, y ningún lote visible con daños D03–D08 ni título pendiente D10.
