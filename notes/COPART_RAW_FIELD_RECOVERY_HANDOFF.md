# Copart raw-field recovery handoff

## Objetivo

Recuperar datos reales ya preservados en el payload `_raw_source` de Copart cuando el campo canónico quedó vacío o incompleto. La implementación no inventa valores, no cambia pesos ni fórmula de scoring y no se ejecuta para IAAI.

## Cambios

- Añadido `CopartRawFieldRecovery`.
- Recupera únicamente desde la fila raw preservada:
  - `Seller Name` y clasificación mediante `SellerTaxonomy`.
  - `Runs/Drives`, conservando raw y normalización existente.
  - `Damage Description` y `Secondary Damage`.
  - `Has Keys-Yes or No`.
  - `Odometer` y `Odometer Brand`.
  - `Sale Title Type` y `Sale Title State`.
  - `Sale Status` y `Sale Light`.
- Nunca sobrescribe un valor canónico no vacío.
- Preserva evidencia de recuperación en `AdditionalData` con claves `source_recovery_*`.
- El selector de candidatos PostgreSQL y el store en memoria calculan el hash sobre el mismo vehículo recuperado/canónico para mantener idempotencia.
- El backfill Copart puntúa el vehículo recuperado y limpiado, no el payload incompleto previo.

## No cambiado

- `LscVehicleScoringEngine` y sus pesos permanecen sin cambios.
- `lsc_pre_grade_v3` permanece como policy version.
- IAAI, Apibara, API, cron, secretos y jobs de IAAI no fueron modificados.
- No se ejecutó deploy ni backfill de producción.

## Validación

`dotnet test inventory-engine/Lsc.Inventory.sln -c Release --no-restore`

Resultado: **166 passed, 0 failed**.

La próxima validación de producción debe reingestar o disponer de payloads raw con los campos faltantes. Si el `_raw_source` antiguo no contiene un campo, ningún código puede recuperarlo retroactivamente; habrá que procesar un Excel que sí lo contenga.
