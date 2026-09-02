# Observabilidad durable Copart

## Alcance

Este componente observa exclusivamente `job-lsc-copart-auto-prod`. No procesa Excel, no modifica el API público, no toca IAAI y no cambia el cron nativo en la primera fase.

## Recursos

- Azure Function App: `func-lsc-copart-watchdog-prod`.
- Identidad administrada propia de la Function.
- Tabla durable de Azure Storage: `CopartExecutionAttempts`.
- Storage de runtime separado del contenedor de archivos fuente si la cuenta existente no cumple los requisitos de Functions.

## Flujo

La Function Timer corre cada cinco minutos. Para cada ventana esperada del cron Copart, crea o actualiza un intento idempotente con `scheduled_at`. Consulta el job automático y asocia la ejecución de Azure cuando existe. Registra `SCHEDULED`, `RUNNING`, `SUCCEEDED`, `FAILED` o `FAILED_INFRA_NO_REPLICA`. El procesador Copart continúa siendo responsable de sus estados internos, heartbeat y contadores definitivos.

La Function no inicia ni detiene ejecuciones en la fase uno. Esto evita duplicar el cron existente y limita su identidad a lectura del job y escritura de su tabla de auditoría. Una fase posterior puede hacer que la Function sea el scheduler único, pero requerirá autorización separada para retirar el cron nativo.

## Estados mínimos

`SCHEDULED`, `RUNNING`, `SUCCEEDED`, `FAILED`, `FAILED_INFRA_NO_REPLICA`, `ABANDONED`, `SKIPPED_LOCK_HELD`.

## Reglas de seguridad

No almacenar VIN, tokens, URLs firmadas, credenciales, payload crudo ni secretos. Los mensajes de error son códigos y resúmenes sanitizados. La Function no recibe permisos sobre `ca-lsc-inventory-api-prod`, jobs IAAI, otros jobs ni secretos.

## Criterios de aceptación

1. Un intento queda registrado aunque Azure no cree una réplica.
2. Una ejecución normal se puede unir por ventana programada y nombre de ejecución Azure.
3. Un fallo de infraestructura se distingue de un fallo de aplicación.
4. Los duplicados y `SKIPPED_LOCK_HELD` no se clasifican como fallos.
5. El cron, API, IAAI y demás jobs permanecen sin cambios.
