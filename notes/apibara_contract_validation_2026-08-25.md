# Validación del contrato Apibara — 2026-08-25

## Fuentes consultadas

- [OpenAPI oficial de Vehicle Auction Data API](https://apibara.tech/openapi/vehicle-auction-data-api.json)
- [Referencia de filtros](https://github.com/apibara-tech/apibara-vehicle-auction-api-examples/blob/main/docs/filters-reference.md)
- [Repositorio oficial de ejemplos](https://github.com/apibara-tech/apibara-vehicle-auction-api-examples)

## Hallazgos aplicados al piloto

El endpoint `GET /vehicles` admite `platform` (`copart` o `iaai`), `facility_id`, `loc_state`, `per_page` con máximo 20 y `lot_sub_status` opcional. La documentación también especifica que `GET /locations` acepta `platform`, `state` y `per_page`; el modelo de ubicación incluye `facility_id`, `name`, `city` y `state`.

Durante la prueba real, Apibara devolvió un error HTTP 500 para `loc_state=FL`: la respuesta del proveedor reveló una consulta interna que referenciaba una columna inexistente `vehicles.facility_state_code`. Para sortear ese defecto del proveedor, el motor resuelve primero un `facility_id` mediante `/locations?platform=copart&state=FL&per_page=1`, y luego consulta `/vehicles` por ese `facility_id`.

La respuesta real de ubicaciones envió `facility_id` como número en vez de texto, y la respuesta de vehículos envió al menos un odómetro como objeto. El motor fue endurecido para aceptar identificadores string/número y valores decimales como número, texto, valor vacío u objeto con propiedad `value`.
