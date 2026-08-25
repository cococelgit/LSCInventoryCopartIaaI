# Validación del buscador simplificado

La interfaz rediseñada cargó 57 lotes desde Azure y mostró 55 resultados bajo los filtros iniciales. El panel izquierdo contiene fuente, rango de años, lista de marcas, puja máxima, solo con puja y solo con fotos reales. Cada resultado derecho conserva foto real, título, lote, ubicación, daño/título cuando existen, fecha de subasta, puja y enlace de ficha en nueva pestaña.

La selección de la marca `ASPT` redujo el listado a un resultado —el lote `80722725`, ASPT Dump Truck— y el control `Limpiar` restableció el listado a 55 resultados. Esto confirma que la lista se filtra en el cliente sin perder la lectura viva proveniente de Azure.

El filtro `Solo con fotos reales` redujo los resultados de 55 a 34 y excluyó los lotes sin evidencia visual. Al limitar el año máximo a `2010` junto con los filtros activos, el listado pasó a cero y mostró un estado vacío con acción de restablecimiento. Se validó así la reacción de filtros por marca, fotos y rango de años.

El control de restablecimiento devolvió el buscador a 55 resultados y reactivó el rango de años predeterminado. La ficha de cada resultado permanece como un enlace que abre una pestaña nueva.

El interruptor `Solo con puja actual` redujo el listado de 55 a 46 vehículos y cambió el resumen operativo a “Con puja actual”. Con ello quedaron probados los filtros de marca, año, fotos y puja directamente sobre el corte vivo de Azure.
