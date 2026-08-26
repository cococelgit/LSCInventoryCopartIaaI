# Recursos oficiales de LSC

Fuente: <https://drive.google.com/drive/folders/1kJQs_rB0_jY2EnR7MN-5FhVNHxNF1F_z>

La carpeta compartida incluye variantes PNG de identidad visual, observadas el 25 de agosto de 2026: `500 x 500- car.png`, `500 x 500- martillo.png`, `500 x 500.png`, `Recurso 1solido azul.png`, `Recurso 1solido blanco.png`, `Recurso 1solido rojo.png`, `Recurso 2lineal blanco.png`, `Recurso 3lineal color.png`, `Recurso 4 vertical color.png`, `Recurso 5 vertical blanco.png`, `Recurso 6 vertical solido color 1.png` y `Recurso 7 vertical solido color 2.png`.

Se seleccionará una variante horizontal o lineal a color para el encabezado oscuro del Inventory UI y una variante blanca como respaldo sobre fondos con azul de marca. La paleta se extraerá de los recursos visuales descargados, sin inferir colores fuera de los activos oficiales.

La variante `Recurso 3lineal color.png` tiene formato horizontal (1200×340) y utiliza una composición de martillo, auto, estrella y contenedor lineal, con el nombre completo La Subasta Cubana en azul y rojo. La variante `Recurso 2lineal blanco.png` tiene formato horizontal (932×265) y está destinada a fondos oscuros.

La extracción de color del archivo oficial identifica azul `#042CD7` y rojo `#FF0400` como colores dominantes. Los activos aprobados para esta actualización quedaron publicados en `/manus-storage/lsc-logo-lineal-color_454249f6.png` y `/manus-storage/lsc-logo-lineal-blanco_435d949d.png`.

El logo blanco oficial se aplicó en los encabezados azul profundo del catálogo y la ficha. Los tokens del UI ahora usan azul `#042CD7`, azul profundo `#031B86` y rojo `#FF0400`. Las pruebas unitarias del bridge y la seguridad server-side aprobaron: 4 archivos y 5 pruebas.

El preview del checkpoint `12dd515a` mostró el logo oficial blanco sobre azul profundo y el rojo de marca. La primera lectura del dominio público siguió mostrando la identidad anterior, por lo que la propagación del despliegue se mantiene pendiente de una segunda verificación antes del cierre.

Una segunda lectura pública con parámetro de caché también mostró la identidad anterior; por tanto, el checkpoint está correcto en preview, pero la propagación del dominio continúa pendiente y no se marca como validada todavía.

La propagación se confirmó posteriormente en `lsc-inv-revi-zyn4tlbw.manus.space`: el encabezado público muestra el logo lineal blanco oficial sobre azul profundo, conserva el acento rojo oficial y el catálogo continúa mostrando los datos y fotos reales de Azure.

La validación final se hizo con el dominio público y su HTML renderizado guardado en `lsc-inv-revi-zyn4tlbw.manus.space__brand_12dd515a-final_1787677958655.html`. La captura muestra el logo lineal blanco oficial, el encabezado azul profundo, el acento rojo y 57 vehículos del corte cargados desde Azure.
