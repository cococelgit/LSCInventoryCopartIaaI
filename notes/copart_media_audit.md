# Auditoría de media Copart

Fecha de revisión: 2026-08-27.

El CSV Copart contiene las columnas `Image Thumbnail` e `Image URL`. En el snapshot local de 146,248 filas, había 146,232 filas con miniatura y 146,247 con `Image URL`. Las URLs válidas observadas apuntan mayoritariamente a `inventoryv2.copart.io/v1/lotImages/{lotId}` y llevan parámetros de contexto (`brand`, `country`, `yardNumber`). El adaptador vigente solo conserva esos dos enlaces directos como máximo, sin resolver el catálogo de fotos que posiblemente devuelva `Image URL`.

Una solicitud HTTP sin sesión a una URL de media devolvió `403`. La navegación directa a `copart.com` desde esta sesión también quedó bloqueada por el servicio de seguridad del proveedor. No se debe intentar evadir ese bloqueo. La solución debe usar únicamente la entrega autorizada que ya exista en el CSV descargado/entorno Azure o requerir acceso aprobado por el proveedor para resolver imágenes.

