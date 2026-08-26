# Política de elegibilidad de inventario — v3

La política se ejecuta como código determinístico antes de persistir un lote en `auction_lots`. No utiliza análisis visual ni consulta fuentes externas.

| Campo autorizado | Campo Apibara aprobado |
|---|---|
| `vin` | `vin` |
| `sale_date` | `auction.auction_at` |
| `location_state` | `location.state`, `facility.state` o estado explícito de la facility consultada |
| `seller_name` | `seller.name` |
| `damage_description` | `condition.primary_damage` |
| `secondary_damage` | `condition.secondary_damage` |
| `sale_title_type_label` | `sale_document.name` |
| indicador de título pendiente | `sale_document.is_pending` |
| notas oficiales | `title_notes`, `special_note`, `announcements`, si Apibara las entrega |

Se descarta únicamente por D00A, D00B, D01–D08 y D10. **D09 queda retirada:** Rebuilt y todos los demás tipos de título se cargan. La ausencia de vendedor o notas no descarta ni marca.

`Certificate of Destruction`, `Junk`, `Non-Repairable` y `Parts Only` permanecen en la base de datos. El UI los oculta por defecto y permite incluirlos mediante el filtro de tipo de título.
