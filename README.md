# La Subasta Cubana — Inventory Platform

Repositorio único del sistema de inventario de **La Subasta Cubana**.

| Directorio | Responsabilidad |
|---|---|
| `/client`, `/server`, `/shared` | Inventory UI React/tRPC, buscador, filtros, carrusel, paginación y auditoría interna. |
| `/inventory-engine` | API y jobs .NET 8, persistencia PostgreSQL/Blob, elegibilidad, normalización, reconciliación e integración IAAI. |
| `/scripts` | Validaciones reproducibles de producción y del proveedor. |
| `/drizzle` | Esquema del servicio web de Manus. |

## Política de fuentes

| Fuente | Entrada autorizada |
|---|---|
| IAAI | Apibara, exclusivamente desde el Inventory Engine. |
| Copart | Snapshot Excel descargado al servidor, exclusivamente mediante `ICopartExcelSnapshotAdapter`. |

El cliente Apibara rechaza cualquier plataforma distinta de `iaai`. Copart no debe reactivarse en los manifests Apibara.

## Validación

```bash
# UI y bridge
pnpm install
pnpm check
pnpm test

# Inventory Engine
dotnet test inventory-engine/Lsc.Inventory.sln -c Release
```

## Seguridad

No se versionan archivos `.env`, tokens, llaves, connection strings ni credenciales. Azure consume secretos desde Key Vault mediante identidad administrada; el UI usa el token de inventario únicamente en el bridge server-side.

## Integración Copart Excel

La siguiente tarea debe comenzar en [`COPART_EXCEL_HANDOFF.md`](./COPART_EXCEL_HANDOFF.md). El contrato ya existe en `inventory-engine/src/Lsc.Inventory.Api/Sources/InventorySourcePolicy.cs`; no debe duplicarse ni conectarse Copart a Apibara.
