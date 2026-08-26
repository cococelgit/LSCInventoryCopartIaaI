# Validación del piloto IAAI de 1,000 vehículos

## Separación de fuentes

Copart quedó deshabilitado en Apibara. El job programado conserva `Sync__Enabled=false` y la barrera de código solo autoriza `iaai` en el cliente Apibara. Copart queda reservado para `ICopartExcelSnapshotAdapter`, que recibirá el Excel descargado por el proceso separado.

## Resultado del piloto

| Métrica | Resultado |
|---|---:|
| Inicio UTC | 2026-08-25 23:42:33 |
| Fin UTC | 2026-08-25 23:45:52 |
| Duración | 3m 19s |
| Lotes observados | 1,133 |
| Vehículos cargados | 1,000 |
| Vehículos marcados | 403 |
| Descartados | 133 |
| Cuarentena | 0 |
| Solicitudes list-only | 57 |
| Fallos | 0 |

La cuota pasó de 26,052 a 25,994 solicitudes restantes: consumo neto de 58, incluyendo la consulta de uso posterior al piloto.

## Cobertura persistida del corte IAAI

| Campo | Cobertura |
|---|---:|
| Plataforma `iaai` | 1,000 / 1,000 |
| Fotos | 1,000 / 1,000 |
| Estado | 1,000 / 1,000 |
| Tipo de título | 1,000 / 1,000 |
| Host de fotos | `vis.iaai.com` |
| Payloads raw IAAI en Blob privado | 1,000 |

El conteo Blob se ejecutó dentro del Container Apps Environment mediante identidad administrada, porque el Storage Account mantiene acceso público deshabilitado. La comprobación devolvió `iaai_blob_count=1000`; el job temporal fue eliminado inmediatamente después.

Copart histórico proveniente de Apibara fue despublicado mediante reconciliación sin borrar registros ni versiones. La política futura despublica después de tres snapshots completos consecutivos sin presencia y reactiva al reaparecer.

## Reglas observadas

El piloto activó D00C, D02, D03, D04 y D05 como descartes; M00, M01, M03, M04 y M05 como marcas. D00D, Q01 y Q04 cuentan con pruebas unitarias explícitas. D09 permanece desactivada: ningún tipo de título se descarta solo por su categoría.

## Pruebas

- Motor .NET: 55 pruebas aprobadas.
- UI/bridge: 33 pruebas aprobadas, incluida selección de IAAI con el corte vivo, Copart=0, fotos, facilities, estados, títulos especiales y paginación.
