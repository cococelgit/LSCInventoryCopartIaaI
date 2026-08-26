# Filtros de daño, título y transmisión

Los tres filtros se derivan del payload vivo de Azure. En el corte validado de 57 lotes, los tres campos llegaron como `No reportado`; por tanto, la interfaz no muestra valores ficticios. La lógica y los conteos se actualizan automáticamente cuando el feed entregue valores como tipos de daño, estados de título o transmisiones.

La prueba unitaria de normalización valida que las opciones del feed se agrupen por valor y conteo. La batería actual aprobó 5 archivos y 6 pruebas.

La validación visual sobre el bridge local confirmó que el sidebar muestra los tres grupos solicitados con sus conteos reales: `No reportado · 57` para daño y título, y `No reportada · 57` para transmisión. El listado siguió cargando 55 resultados desde el corte de 57 lotes; las opciones se actualizarán automáticamente cuando el proveedor envíe valores específicos.

La página de producción cargó 57 lotes y 55 resultados desde Azure después del despliegue. La verificación del sidebar se completa con la inspección de la estructura publicada, ya que sus grupos adicionales quedan dentro del área desplazable de filtros.

La primera lectura pública posterior al checkpoint todavía mostró la estructura anterior del sidebar; por ello la publicación se mantiene pendiente de propagación y no se marca finalizada hasta comprobar que el HTML público incluya los tres grupos nuevos.

La verificación final en `lsc-inv-revi-zyn4tlbw.manus.space` confirmó la propagación: los grupos `Tipo de daño`, `Estado del título` y `Transmisión` están presentes en producción, con sus conteos reales, mientras el listado conserva 55 resultados del corte Azure.
