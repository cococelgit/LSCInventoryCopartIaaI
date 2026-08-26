# Fuentes verificadas para la carga IAAI

## Documentación oficial

1. [Apibara Vehicle Auction Data API documentation](https://apibara.tech/en/products/vehicle-auction-data-api/docs)
2. [Apibara Vehicle Auction Data API endpoints](https://apibara.tech/en/products/vehicle-auction-data-api/endpoints)
3. [Apibara OpenAPI schema](https://apibara.tech/openapi/vehicle-auction-data-api.json)
4. [Apibara Vehicle Auction API AI Agent Guide](https://github.com/apibara-tech/apibara-vehicle-auction-api-examples/blob/main/AI_AGENT.md)

## Hallazgos externos

- `GET /vehicles?platform=iaai` es el endpoint oficial para listar IAAI.
- La colección usa paginación por cursor mediante `meta.next_cursor`; el cursor debe reutilizarse sin construirlo manualmente.
- El máximo de `per_page` documentado es 20.
- El contrato de colección solo documenta `data` y `meta` con `per_page`, `next_cursor` y `prev_cursor`; no documenta un campo de total.
- El endpoint soporta filtros por `loc_state`, `facility_id`, fechas, estado del lote y otros campos.
- La documentación recomienda checkpoints de cursor e incremental synchronization.
- Los datos generales pueden refrescarse aproximadamente cada 30 minutos; la disponibilidad de campos depende de la fuente.
- Las claves deben permanecer server-side y deben manejarse respuestas 401, 403, 404, 422 y 429.

## Observación directa del plan LSC — 2026-08-25

- Plan: Basic.
- Límite por página: 20.
- Cuota: 30,000 solicitudes; 378 usadas; 29,622 disponibles al medir.
- Una página IAAI devolvió 20 lotes y `next_cursor`, sin total.
- La página inspeccionada incluyó instalaciones IAAI de varios estados, confirmando que `platform=iaai` sin estado es alcance nacional.
