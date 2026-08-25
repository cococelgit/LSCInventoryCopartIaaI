# Mapeo de filtros BidCars → LSC

| Filtro de referencia | Control LSC | Estado del corte Azure |
|---|---|---|
| Año | Rango mínimo–máximo | Disponible |
| Auction type | Fuente Copart / IAAI | Copart activo; IAAI reservado |
| Odómetro | Rango mínimo–máximo en millas | Campo presente, sin valores en el corte actual |
| Marca y modelo | Selección múltiple dinámica | Disponible |
| Body style | Tipo de vehículo / carrocería | Disponible: automóvil, SUV, sedán, pickup, buses y otros |
| Loss type | Tipo de daño | Campo presente, sin valores actuales |
| Start code | Código de arranque | No provisto: se muestra como `No reportado` |
| Drive type | Tipo de tracción | Campo presente, sin valores actuales |
| Transmission | Transmisión | Campo presente, sin valores actuales |
| Fuel type | Tipo de combustible | Campo presente, sin valores actuales |
| Title status | Estado del título | Campo presente, sin valores actuales |
| Bid price | Puja máxima | Disponible |

La interfaz no sustituye campos ausentes con otros campos de significado diferente. Cada control se activa automáticamente cuando el proveedor entregue valores reales.

## Validación del corte vivo

El sidebar publicado en preview cargó 57 lotes y expuso los nuevos controles. Se seleccionó el modelo `2026 24 PCS FLOOR ANCHOR POTS` y el listado se redujo de 55 a 1 vehículo, confirmando que los filtros adicionales aplican sobre el corte Azure. Los campos sin valores específicos conservan su opción `No reportado` o `No reportada` sin crear categorías artificiales.
