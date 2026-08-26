# LSC Inventory Engine

Backend .NET 8 de inventario para **La Subasta Cubana**. Ejecuta ingestión controlada, normalización canónica, política de elegibilidad auditable, persistencia PostgreSQL/Blob, reconciliación y API protegida.

## Estado actual

| Componente | Estado |
|---|---|
| IAAI mediante Apibara | Implementado; job manual/piloto separado. |
| Copart mediante Apibara | Prohibido por código y configuración. |
| Copart Excel | Contrato `ICopartExcelSnapshotAdapter` listo para la implementación externa. |
| PostgreSQL y Blob privado | Implementados. |
| Política de elegibilidad v4 | Implementada y auditada; D09 desactivada. |
| Reconciliación | Tres snapshots completos sin presencia; reactivación al reaparecer. |
| API de lectura | Protegida; expone inventario, validación, uso y descartes paginados. |

## Comandos

```bash
dotnet restore Lsc.Inventory.sln
dotnet test Lsc.Inventory.sln -c Release
dotnet build Lsc.Inventory.sln -c Release
```

## Configuración segura

La clave de Apibara, el token de servicio y las credenciales de persistencia nunca se almacenan en el repositorio. Producción usa Key Vault e identidad administrada. Los manifests de `infra/` contienen únicamente referencias y configuración no sensible.

## Fuentes

`InventorySourcePolicy.RequireApibaraPlatform` acepta únicamente `iaai`. Copart debe entrar por `ICopartExcelSnapshotAdapter`; consulte `../COPART_EXCEL_HANDOFF.md` desde la raíz del repositorio.
