# Política v4 — Admisión, limpieza y publicación de inventario

## Enrutamiento de fuentes

| Fuente | Entrada autorizada | Entrada prohibida |
|---|---|---|
| Copart | Snapshot Excel descargado al servidor y aceptado por F01–F06 | Apibara y cualquier consulta Copart desde el API de Apibara |
| IAAI | Apibara, mediante job independiente y cursores reanudables | Excel Copart o mezcla con el job Copart |

Ambas fuentes convergen en el mismo contrato canónico, evaluador, normalizador comercial, reconciliador y publicador. Cada adaptador conserva el payload o fila raw original.

## Precedencia

La secuencia obligatoria es: aceptar snapshot o partición completa → parseo → normalización técnica mínima → cuarentena técnica Q01/Q04 → descartes D00A–D08/D10 → limpieza comercial de sobrevivientes → marcas M00–M08 → reconciliación → publicación atómica.

Una cuarentena técnica impide publicar esa fila. Un descarte conserva auditoría y no entra al inventario activo. Una marca no bloquea la carga. Se guardan todas las reglas activadas.

## Reglas técnicas y de descarte

| Código | Regla | Resultado |
|---|---|---|
| Q01 | Lote ausente o no normalizable | Cuarentena técnica |
| D00A | VIN ausente | Descartar |
| D00B | Fecha de venta ausente o inválida | Descartar |
| D00C | Modelo 1981+ con VIN distinto de 17 caracteres, caracteres prohibidos o check digit inválido cuando aplica | Descartar |
| M00 | Modelo 1980 o anterior con VIN legacy válido | Cargar y marcar `VIN_LEGACY` |
| D00D | Fecha de venta anterior al día corriente en la zona de la subasta | Descartar del inventario activo |
| Q04 | Año ausente, no numérico o fuera de 1900–año corriente + 1 | Cuarentena técnica |
| D01 | Estado WI, AL o MI | Descartar |
| D02 | Vendedor WHEELZY, MARESTAR, TITLEMAX o CARBRAIN | Descartar |
| D03–D08 | Daños explícitos aprobados en la política v3 | Descartar |
| D10 | Pending title, repo affidavit o duplicate title acompañado de indisponibilidad | Descartar |

**D09 queda desactivada.** Rebuilt, Certificate of Destruction, Junk, Non-Repairable, Parts Only y cualquier otra categoría de título se cargan si no activan otra regla. El UI puede ocultarlas por defecto, pero el tipo de título por sí solo nunca descarta.

## Marcas informativas

| Código | Marca |
|---|---|
| M01 | `SELLER_NOT_DISCLOSED` |
| M02 | `TITLE_CODE_UNMAPPED` — aplicable al Excel Copart cuando corresponda |
| M03 | `NO_KEYS` |
| M04 | `RUN_STATUS_NOT_VERIFIED` |
| M05 | `ODOMETER_UNVERIFIED` |
| M06 | `NO_THUMBNAIL` |
| M07 | `CONDITIONAL_SALE` |
| M08 | `MODEL_UNRESOLVED` |

## Limpieza comercial

Cada campo conserva una representación `raw` y otra `normalized`. La limpieza normaliza mayúsculas y espacios, aplica alias exactos versionados a marca/modelo, separa modelo de trim, mantiene fecha UTC y zona fuente, rechaza importes negativos, conserva unidades, y elimina parámetros de sesión de los logs de imágenes. La evidencia raw no se sobrescribe.

## Reconciliación

La identidad es `source:lot_number`. Un lote elegible nuevo crea `FIRST_SEEN`. Un cambio material crea versión; un lote sin cambio solo actualiza `last_seen_at`. Un lote ahora descartado se despublica sin borrarse. D00A/D00B corregidos pueden reactivarse automáticamente; la desaparición de D01–D10 requiere revisión. La ausencia del feed se despublica después de tres snapshots aceptados consecutivos o 60 minutos, lo que ocurra después. El mismo VIN en fuentes distintas mantiene listings separados y vinculados.
