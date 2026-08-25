# Filtros de daño, título y transmisión

Los tres filtros se derivan del payload vivo de Azure. En el corte validado de 57 lotes, los tres campos llegaron como `No reportado`; por tanto, la interfaz no muestra valores ficticios. La lógica y los conteos se actualizan automáticamente cuando el feed entregue valores como tipos de daño, estados de título o transmisiones.

La prueba unitaria de normalización valida que las opciones del feed se agrupen por valor y conteo. La batería actual aprobó 5 archivos y 6 pruebas.

La validación visual sobre el bridge local confirmó que el sidebar muestra los tres grupos solicitados con sus conteos reales: `No reportado · 57` para daño y título, y `No reportada · 57` para transmisión. El listado siguió cargando 55 resultados desde el corte de 57 lotes; las opciones se actualizarán automáticamente cuando el proveedor envíe valores específicos.
