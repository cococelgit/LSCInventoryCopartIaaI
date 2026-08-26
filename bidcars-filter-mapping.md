# Matriz funcional BidCars → LSC

Esta matriz usa BidCars únicamente como referencia de arquitectura de información e interacción. La implementación conservará identidad, textos, decisiones comerciales y código propios de La Subasta Cubana.

| Filtro/función de referencia | Evidencia IAAI | Estado LSC actual | Implementación objetivo |
|---|---|---|---|
| Búsqueda lote/VIN/título | Documentado `s` | Disponible | Conservar búsqueda directa y acceso a títulos especiales. |
| Estado de subasta | `lot_status`, `lot_sub_status`, timed/buy-now | Parcial | Añadir Open, Live, Timed, Buy Now y Ended cuando existan. |
| Precio estimado | `pricing.estimated_cost.from/to` | Disponible en payload, no público | Añadir rango separado de puja y presupuesto LSC. |
| Fuente | IAAI; Copart futuro por Excel | Disponible | Conservar selector dinámico. |
| Año | `year_from/to` | Disponible | Conservar rango. |
| Marca/modelo | `make`, `model` | Disponible | Conservar multiselección dependiente. |
| Tipo de vehículo | Metadata con 30 tipos | Disponible | Mostrar opciones dinámicas con conteos. |
| Body style | `vehicle_specs.body_style` | Disponible en payload, no público | Añadir filtro separado del tipo general. |
| Odómetro | `odometer_from/to`, mi/km/status | Disponible | Añadir estado de odómetro en ficha. |
| Start code | `condition.run_condition` | Persistido, no público | Exponer y filtrar Run and Drive, Vehicle starts, Stationary/No information. |
| Llave | `condition.has_key`, `has_key` | Persistido, no público | Añadir filtro y badge. |
| Tracción | `drive_type` | Disponible | Conservar. |
| Transmisión | `transmission` | Disponible | Conservar. |
| Combustible | `fuel_type` | Disponible | Conservar. |
| Loss type | `condition.loss`, `LossTypeDesc` | Parcial | Exponer cuando exista; no sustituir por primary damage. |
| Primary/secondary damage | `condition.primary_damage/secondary_damage` | Primary disponible | Exponer ambos y filtrar daño. |
| Color exterior | `vehicle_specs.exterior_color` | Disponible | Añadir filtro dinámico. |
| Motor: litros | `engine.size_l` | Disponible en payload, no público | Añadir rango. |
| Motor: HP | `engine.hp` | Disponible en payload, no público | Añadir rango. |
| Motor: layout | Inline/V/W/Boxer | Disponible en payload, no público | Añadir selección múltiple. |
| Cilindros | Metadata 1–12 | Disponible en payload, no público | Añadir selección. |
| Documento/título | type/group/pending/export/registration | Parcial | Conservar tipo y añadir pending/export/registration. |
| Tipo de vendedor | insurance/non-insurance/dealer/finance | Persistido, no público | Añadir filtro. |
| Ubicación/facility/estado | display/branch/state/zip | Disponible | Conservar y añadir ZIP/radio cuando sea útil. |
| Shipping disponible | `has_shipping_price` | Endpoint oficial | Mantener fuera del primer release; requiere llamada/costo adicional. |
| Fotos/360/video | thumbs/items/has_360/has_video | Fotos disponibles | Añadir badges 360/video y media grande. |
| Orden | fecha, puja baja/alta | Disponible | Añadir año, odómetro y precio estimado. |
| Paginación | Cursor proveedor; paginación UI | Disponible | Mantener 24 por página y preparar server-side para inventario nacional. |

## Ficha BidCars → LSC

| Sección | Campo LSC objetivo | Disponibilidad IAAI |
|---|---|---|
| Resumen superior | Start code, llave, transmisión, combustible, tracción, motor, odómetro | Disponible |
| Identidad | Año/marca/modelo/series, VIN enmascarado, lote, fuente | Disponible; VIN completo solo bajo política interna |
| Venta | Facility, branch, lane/aisle, seller, fecha, estado | Disponible |
| Condición | Loss, daños primario/secundario, airbags, VIN status | Disponible parcial/variable |
| Especificaciones | Body, color, engine, HP, cilindros, país, class, score, options | Disponible parcial/variable |
| Documento | Nombre, tipo, grupo, pending, export, registration, brand/notes | Disponible |
| Precio | Puja, buy now, estimación proveedor y presupuesto LSC | Disponible parcial |
| Media | Fotos grandes, thumbnails, 360 y video | Disponible parcial |
| Historial/similares | Endpoints `/history` y `/related` | Disponible bajo demanda; no cargar masivamente |

## Campos no observados o no prioritarios

No se inventarán customs europeos, shipping internacional genérico, “market value” propio de terceros, ratings editoriales ni historiales externos. Teléfonos, coordenadas exactas, IDs internos y enlaces operativos de IAAI permanecerán privados.

## Evidencia

- Auditoría pública BidCars: `bidcars-functional-audit.md`.
- Catálogo IAAI: `iaai-field-matrix.md`.
- Metadata oficial: documentación y repositorio `apibara-tech/apibara-vehicle-auction-api-examples`.
