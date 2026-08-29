# Contrato Run & Drive de Copart

## Propósito y alcance

Este contrato aplica únicamente a los lotes Copart recibidos desde el archivo CSV/CSV.GZ descargado al servidor. La fuente de la condición es exclusivamente la columna exacta **`Runs/Drives`**. No consulta Apibara para Copart y no interpreta `Drive` o `DriveType`, que siguen representando solo el tipo de tracción.

> **Run & Drive no garantiza que el vehículo funcione al recogerlo, que sea seguro para conducir, ni que no necesite reparación.** Es únicamente la condición declarada por Copart en el archivo en el momento de la observación.

## Campos públicos finales

| Campo JSON público | Tipo | Origen | Regla |
|---|---|---|---|
| `runCondition` | `string` | `VehicleCondition.RunCondition.Normalized` | Código operativo normalizado. Para Copart nunca es nulo: usa `UNVERIFIED` si no hay una declaración explícita reconocida. |
| `runConditionRaw` | `string \| null` | `VehicleCondition.RunCondition.Raw` | Texto original de la columna `Runs/Drives`; se conserva casing y contenido de la fuente, salvo trimming estándar del CSV. |
| `driveType` | `string \| null` | Columna `Drive` | Tracción. No se usa para generar ni inferir `runCondition`. |

En el payload/snapshot serializado interno, los mismos valores se almacenan como `condition.run_condition.run_condition` y `condition.run_condition.run_condition_raw`. Los campos legados internos `value` y `label` se aceptan solamente para lectura de payloads históricos y no se vuelven a serializar.

## Normalización permitida

| Valor de `Runs/Drives`, sin importar mayúsculas/minúsculas | `runCondition` |
|---|---|
| `RUN & DRIVE` o `RUNS AND DRIVES` | `RUNS_AND_DRIVES` |
| `STARTS` o `ENGINE START PROGRAM` | `STARTS` |
| `STATIONARY` | `STATIONARY` |
| Vacío, `NO INFORMATION` o cualquier otro valor | `UNVERIFIED` |

No se permite inferir Run & Drive a partir de llaves, daños, título, historial de pujas, score, fotos ni cualquier campo distinto de `Runs/Drives`.

## Ejemplo JSON sanitizado

El siguiente ejemplo se obtuvo de una fila real de referencia Copart. Se eliminaron el VIN, identificador del lote, vendedor y URL de imágenes. El valor crudo `DEFAULT` no pertenece a la lista explícita; por ello conserva su texto en `runConditionRaw`, pero se normaliza de manera conservadora a `UNVERIFIED`.

```json
{
  "lot": "copart:REDACTED",
  "platform": "copart",
  "year": 2013,
  "make": "ACURA",
  "model": "TSX",
  "driveType": "FRONT WHEEL DRIVE",
  "runCondition": "UNVERIFIED",
  "runConditionRaw": "DEFAULT"
}
```

## Uso posterior del portal

El agente de portal/API debe consumir `runCondition` para la faceta, filtro y señal visual. `runConditionRaw` debe emplearse solo como evidencia/transparencia en la ficha o tooltip. La faceta no debe agrupar por raw text, ya que introduciría variantes de origen y valores no verificados.

La UI puede presentar `RUNS_AND_DRIVES` como “Runs & Drives según Copart”, `STARTS` como “Enciende según Copart”, `STATIONARY` como “Estacionario según Copart” y `UNVERIFIED` como “Condición de marcha no verificada”. El disclaimer anterior debe acompañar cualquier estado visible.

## Estado de esta tarea

La implementación y las pruebas locales están listas. No se modificó infraestructura, no se inició un job y no se desplegó API, portal ni imágenes de contenedor. El agente de API/portal deberá confirmar este contrato antes de su propio despliegue.
