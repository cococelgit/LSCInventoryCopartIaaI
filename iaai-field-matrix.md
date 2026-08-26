# Matriz de campos IAAI

Catálogo generado el 26 de agosto de 2026 mediante una página de 20 lotes abiertos y cinco respuestas de detalle. Se realizaron seis solicitudes y se omitieron deliberadamente VIN, lote y valores en el artefacto de análisis. El endpoint general contiene 310 rutas detectadas y el payload mantiene atributos internos adicionales.[1]

## Principio de datos

El sistema conservará el payload completo versionado en Blob privado y el JSON de snapshot para auditoría. PostgreSQL y el contrato público normalizarán únicamente campos útiles para búsqueda, evaluación y presentación. Teléfonos, direcciones operativas, coordenadas exactas, enlaces internos, IDs de proveedor y atributos técnicos sin uso comercial no se expondrán al navegador.

## Contraste con documentación oficial

La referencia oficial confirma endpoints adicionales para historial, relacionados, metadata de filtros, shipping y resolución de URLs, además de lista y detalle.[2] La guía de filtros documenta búsqueda textual, estado de lote, fechas, precio, tipo, color, combustible, odómetro, motor, potencia, transmisión, cilindros, tracción, condición de marcha, daño, llave, ZIP/radio, facility, estado, oficina, documento, vendedor y disponibilidad de shipping.[3]

El metadata consultado directamente expone grupos para `lot`, `auction_type`, `make_model`, `types`, `ranges`, `color`, `fuel_type`, `transmission`, `drive_type`, `running_condition`, `damage`, `cylinders`, `engine_type`, `has_key`, `auction_date`, `sale_document_filters`, `seller_type`, `shipping` y `location_filters`. Los rangos informados son precio 0–250,000 USD, año 1900–2027, odómetro 0–500,000 mi, motor 0–20 L y potencia 0–3,000 HP.

## Campos normalizados prioritarios

| Grupo | Campos IAAI observados | Cobertura muestra | Destino |
|---|---|---:|---|
| Identidad | `platform_id`, `slug_vin`, `subLot`, `ad` | 20/20 | PostgreSQL/API |
| Subasta | `auction_at`, `state`, `is_buy_now`, `is_timed`, `countdown`, `formatted`, `full_date` | 20/20 | PostgreSQL/API/UI |
| Precio | `current_bid_usd`, `current_bid2_usd`, `buy_now_usd`, `estimated_cost.from/to/text` | 16–20/20 | PostgreSQL/API/UI |
| Ubicación | `location.display`, `location.send_from`, branch/city/state/zip | 20/20 | PostgreSQL/API/UI |
| Documento | `name`, `type`, `sale_document_group`, `is_pending`, `export`, `registration`, `page_id` | 20/20 | PostgreSQL/API/UI |
| Vendedor | `name`, `type`, `class`, `text_class` | 20/20 | PostgreSQL/API/UI |
| Condición | primary/secondary damage, loss, run condition, key, airbags, VIN status | 6–20/20 | PostgreSQL/API/UI |
| Odómetro | miles, kilometers, status/brand/unit | 20/20 | PostgreSQL/API/UI |
| Especificaciones | body style, series, class, score, color, fuel, transmission, drive, engine size/layout/HP/raw, cylinders, restraint, options, country | 18–20/20 | PostgreSQL/API/UI |
| Media | `thumbs`, `items[].large/thumb/type`, `has_360`, `has_video`, `Link360` | 16–20/20 | Blob/API/UI |
| Venta | ACV, estimated repair cost, lane, aisle, selling branch, stock, notes | 1–20/20 | PostgreSQL/API/detail |

## Campos del listado que hoy no se modelan completamente

| Ruta | Uso propuesto |
|---|---|
| `pricing.estimated_cost.from/to/text` | Filtro y rango de estimación del proveedor, separado del presupuesto LSC. |
| `vehicle_specs.body_style` | Filtro Body Style. |
| `vehicle_specs.engine.hp/layout/raw/size_l` | Filtros litros, potencia, tipo de motor y ficha. |
| `vehicle_specs.airbags/restraint_system` | Ficha de seguridad. |
| `location.send_from` | Ficha y futura logística. |
| `sale_document.type/group/export/registration` | Filtro de documento y ficha. |
| `seller.class/text_class` | Advertencia y segmentación de vendedor. |
| `media.items` y `has_video` | Galería de alta resolución, tipos de media y badge de video. |
| `condition.loss` | Filtro Loss Type. |
| `auction.is_buy_now/is_timed` | Estados Fast Buy/Timed. |

## Datos internos que se conservarán pero no serán públicos

`details.attributes` incluye teléfonos, direcciones, coordenadas, IDs internos, enlaces operativos, flags de tenant y metadatos de proveedor. Permanecerán en Blob/JSON privado para auditoría. El API público solo expondrá una lista permitida y tipada para evitar filtrar datos operativos o acoplar el UI al payload crudo.

## Referencias

[1]: https://apibara.tech/en/products/vehicle-auction-data-api/docs "Apibara Vehicle Auction Data API documentation"
[2]: https://github.com/apibara-tech/apibara-vehicle-auction-api-examples/blob/main/docs/endpoints-reference.md "Apibara official endpoints reference"
[3]: https://github.com/apibara-tech/apibara-vehicle-auction-api-examples/blob/main/docs/filters-reference.md "Apibara official filters reference"
