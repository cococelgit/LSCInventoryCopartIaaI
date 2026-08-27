# Auditoría de media Copart

Fecha de revisión: 2026-08-27.

El CSV Copart contiene las columnas `Image Thumbnail` e `Image URL`. En el snapshot local de 146,248 filas, había 146,232 filas con miniatura y 146,247 con `Image URL`. Las URLs válidas observadas apuntan mayoritariamente a `inventoryv2.copart.io/v1/lotImages/{lotId}` y llevan parámetros de contexto (`brand`, `country`, `yardNumber`). El adaptador vigente solo conserva esos dos enlaces directos como máximo, sin resolver el catálogo de fotos que posiblemente devuelva `Image URL`.

Una solicitud HTTP sin sesión a una URL de media devolvió `403`. La navegación directa a `copart.com` desde esta sesión también quedó bloqueada por el servicio de seguridad del proveedor. No se debe intentar evadir ese bloqueo. La solución debe usar únicamente la entrega autorizada que ya exista en el CSV descargado/entorno Azure o requerir acceso aprobado por el proveedor para resolver imágenes.

## Evidencia desde Azure

Una sonda ejecutada desde el entorno Azure autorizado sobre doce URLs de catálogo incluidas en el snapshot obtuvo once respuestas `200` y una respuesta `404`. La latencia mediana fue 236.82 ms y el percentil 95 fue 756.65 ms. Las once respuestas correctas reportaron un promedio de 12.09 imágenes de galería y 12.09 enlaces HD; cada objeto de imagen expone variantes mediante `lotImages[].link[]`, incluidas las señales `isThumbNail` e `isHdImage`.

El primer lote controlado de enriquecimiento procesó 1,000 lotes existentes. Resolvió 993 galerías HD en 57.76 segundos y registró siete fallos no fatales: URLs `404` o catálogos sin enlace directo seguro. El worker conserva una imagen HD por secuencia cuando existe, usa imagen estándar o miniatura únicamente como fallback, y no altera elegibilidad, timestamps de venta, reconciliación ni IAAI.

El enriquecimiento se ejecutará en un Job Copart dedicado, separado de descarga e ingesta principal. Su flujo no depende de Apibara ni consulta endpoints de listados distintos a la URL de catálogo presente en el archivo Copart aprobado.
