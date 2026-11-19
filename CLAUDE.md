# ClonDisk

**ClonDisk** es una aplicación de escritorio para Windows 10 y 11 que clona discos y
particiones entre HDD y SSD (HDD↔SSD, HDD↔HDD, SSD↔SSD; SATA, M.2 y NVMe, internos o por USB),
incluido el disco del sistema: al retirar el disco original y poner el clon, el equipo debe
arrancar y funcionar exactamente igual.

**Etapa 4 (ajuste de particiones, RF-13/16/18) implementada:** distribución en el destino,
tabla reconstruida, ampliación de NTFS e identificadores nuevos con reparación del arranque.
Pasan 158 pruebas unitarias y las 17 de integración como administrador (incluidas ampliar un
VHD de 64 a 128 MB y clonar con identificadores nuevos junto al original). Sin probar: la
reparación del arranque de un clon de Windows con identificadores nuevos, que queda para la
prueba manual del usuario. El 15/11/2026 el usuario empezó la etapa 5, con lo que la 4 queda
aceptada; sus pruebas de arranque (etapas 3 y 4) siguen pendientes.

**Etapa 5 en curso** (destino más pequeño, RF-14, y conversión MBR↔GPT, RF-17): implementada en
el motor, la CLI (`--gpt`/`--mbr`) y la interfaz. Pasan 190 pruebas unitarias y las de
integración como administrador (incluidas reducir un VHD de 128 a 64 MB con flujos alternativos y
enlaces duros, y convertir un VHD de GPT a MBR y de vuelta a GPT). El 17/11/2026 se probó en real
(ver «Prueba real del 17/11/2026»): el flujo funciona, pero no se llegó a terminar un clonado
completo. Pendiente: la prueba manual de arranque (clon de Windows a un disco menor y clon
convertido, también el código de arranque MBR propio), que hará el usuario. Limitación: al
convertir a MBR solo se marca activa la partición del Windows en uso.

**Publicado el 17/11/2026** (ver «Producto y distribución» → «Publicación»): repositorio público
en GitHub con licencia GPL-3.0, Releases con el .zip y web en
https://changli-ai.github.io/ClonDisk/, ya verificada en Google Search Console. **Versión actual:
0.1.3** (publicada el 19/11/2026). La 0.1.1 corrigió la verificación fallida «a partir del byte 0»,
la 0.1.2 la de los bloques de C: (Windows montaba el clon mientras se verificaba) y la 0.1.3 añade
el aviso destacado al terminar y «Reparar el arranque»; ver «Prueba real del 18/11/2026». Con la
0.1.2 el clon se completó y verificó, pero no arrancó (0xc000000e) porque el usuario lo puso en
línea junto al original antes de cambiar los discos. **El 19/11/2026, tras «Reparar el arranque»
con la 0.1.3, el clon arrancó Windows en el hardware real con los archivos del original** (primera
vez que un clon del disco del sistema hecho con ClonDisk arranca).

**Estado anterior:** etapas 0, 1 y 2 completadas. **Etapa 3 en curso** (clonar el disco del
sistema en caliente): instantáneas VSS, copia inteligente, planificador y ejecutor hechos. Pasan
las 134 pruebas unitarias y las 15 de integración como administrador (con VSS real sobre VHD).

**Pendiente de la etapa 3, por decisión del usuario (15/11/2026): la prueba de arranque del
clon del disco del sistema la hará él manualmente**, en VirtualBox y en hardware real. Se llegó
a lanzar el clon del disco 0 a un VHD dinámico en `C:\ClonDiskPruebas` con la CLI: el plan fue
correcto (unos 355 GB desde instantáneas de C: y Recuperación, EFI y MSR enteras, GPT ajustada
a un destino 680 KB mayor), pero copiar y escribir en el mismo SSD QLC (Intel 660p) iba a 27–70
MB/s y se canceló al 12 %. Recomendación para la prueba: destino en otro disco, al menos 1 MiB
mayor que el origen (el formato VHD redondea su tamaño hacia abajo); en VirtualBox, máquina con
EFI y controladora NVMe. La etapa no está completa hasta que el usuario confirme que arranca.
El 15/11/2026 el usuario decidió empezar la etapa 4 con esta prueba aún pendiente.
**Avance del 19/11/2026:** un clon del disco del sistema (GPT/UEFI, WD de 250 GB a otro idéntico,
en otro equipo con Windows 10) arrancó en hardware real, pero después de «Reparar el arranque»,
porque se había puesto en línea junto al original. Falta comprobar que arranca **sin reparación**
(RF-11) cuando se apaga y se cambian los discos sin ponerlo en línea, como pide el aviso.

Si el proceso de clonado se termina a la fuerza (no cancelado), sus instantáneas no
persistentes quedan hasta el siguiente reinicio: `vssadmin delete shadows` no puede borrar las
de contexto de copia de seguridad («fuera del contexto permitido»). Cancelar con Ctrl+C o desde
la interfaz sí las libera. Posible mejora: limpiarlas al arrancar la app con `VSS_CTX_ALL` y
`DeleteSnapshots`.

Decisiones del usuario para la etapa 3 (14/11/2026): la copia inteligente (solo bloques en uso
de NTFS) se adelanta desde la etapa 4, porque el clon de prueba del disco del sistema va a un
VHD dinámico en C: y sector a sector no cabría (954 GB frente a 605 GB libres); se permite
crear instantáneas VSS de C: en las pruebas de esta etapa.

**Incidente (14/11/2026):** el equipo se bloqueó (pantalla azul `0x0A`, firma
`AV_nt!KiInsertTimerTable`, lista de temporizadores del núcleo dañada) segundos después de
lanzar `SnapshotTests` como administrador, al crear el VHD. Analizado el volcado con cdb
(`!analyze -v`): el fallo está en `dxgmms2!VIDMM_RECYCLE_HEAP_MGR::AddToUnlockDebounce`, el
gestor de memoria de vídeo de DirectX, en un hilo del proceso `System`; nada de la pila tiene
que ver con discos virtuales, VSS ni la app. Causa probable: el controlador gráfico de Intel
(`igdkmd64.sys`, de agosto de 2021). Se recomendó al usuario actualizarlo.

**Segundo bloqueo (14/11/2026, 17:12):** mismo código `0x0A`, ahora en
`nt!KiRemoveEntryTimer` (al retirar un temporizador, desde una DPC), justo al crear la
instantánea VSS del VHD de origen en `VhdCloneTests`. Las dos veces coincidieron con la
aparición de un disco o volumen nuevo (montar un VHD, crear una instantánea), así que ya no se
trata como casualidad: algún controlador libera un temporizador que sigue activo, y el fallo
salta después donde caiga (la primera vez, en el gestor de vídeo). No hay filtros de disco ni
de volumen de terceros; entre los controladores de terceros cargados hay varios antiguos de
almacenamiento y plataforma: Intel RST `iaStorAC` (2020), Intel `igdkmd64` (2021), Intel DPTF
(2019), Realtek `RtsUer` (2019). Las pruebas con VHD y VSS como administrador y el clon del
disco del sistema quedan en pausa hasta que el usuario decida (actualizar controladores, usar
Driver Verifier o seguir asumiendo el riesgo).

**Causa encontrada (tercer bloqueo, 14/11/2026 17:37, ya con el controlador gráfico
actualizado):** el temporizador dañado está dentro del objeto de dispositivo de `volsnap` (el
controlador de VSS de Windows) sobre el volumen de un VHD de prueba (`!pool` → etiqueta `Devi`;
`!devobj` → `\Driver\volsnap`, encima de `HarddiskVolume7`). Es una carrera de `volsnap`: quitar
un volumen pocos segundos después de liberar una instantánea suya deja un temporizador vivo en
memoria liberada, y el equipo cae al vencer, a veces en otra prueba. Los controladores de Intel
no tenían que ver. Medida: `VirtualDisk.Detach` espera `SnapshotQuietPeriod` (90 s) desde el
último uso de instantáneas (`VirtualDisk.NoteSnapshotUse`). El clon del disco del sistema no
quita C:, pero al desmontar un VHD de destino también hay que dejar pasar ese tiempo. Riesgo
para la app real: que el usuario desconecte un disco de origen externo justo al terminar.

Pendiente de la etapa 1: la lectura de SMART y de BitLocker solo funciona como administrador
y **no se ha comprobado en una consola elevada** (las sesiones de desarrollo no lo son).
Actualiza este archivo cuando una etapa se complete o cambie una decisión.

## Reglas de trabajo

- Comunicación con el usuario y textos de la interfaz: **español**. Identificadores de código: inglés.
- **Nunca ejecutes una operación de escritura contra un disco físico** durante el desarrollo o
  las pruebas. Las pruebas de escritura se hacen solo contra discos virtuales VHD/VHDX creados
  para ello. Cualquier excepción requiere confirmación explícita del usuario en ese momento.
- La app toma como referencia funcional productos comerciales de clonación. El nombre, los
  textos, los iconos y el diseño visual deben ser propios; no se copia nada de ellos.
- El equipo de desarrollo es Windows 10 Home: no hay Hyper-V. Los VHD se montan con
  Administración de discos o `diskpart`; para probar el arranque de un clon se usa VirtualBox
  o un disco físico.
- Avanza una etapa cada vez. No empieces la siguiente sin que la anterior tenga sus pruebas
  pasando y el usuario la haya dado por buena.

## Casos de uso que debe cubrir

1. **Ampliar el disco sin perder datos.** Sustituir el disco actual por un HDD/SSD más nuevo
   y más grande, conservando archivos, programas y sistema operativo.
2. **Migrar Windows 10/11 sin reinstalar.** Pasar la instalación de un HDD a un SSD, o de un
   SSD a otro (SATA, M.2, NVMe), y arrancar desde el nuevo.
3. **Rescatar un disco que falla.** Copiar todo lo legible de un disco con sectores dañados a
   un disco sano.
4. **Llevar el disco a un ordenador nuevo.** Clonar el disco del equipo antiguo y arrancar el
   equipo nuevo desde el clon.
5. **Transferir una partición.** Clonar una sola partición a otro disco o a una unidad externa.
6. **Copia de seguridad de sustitución inmediata.** Mantener un clon completo que pueda
   ocupar el lugar del original si este falla, sin proceso de restauración.
7. **Medio de arranque de rescate.** Crear un USB de arranque (WinPE) con la herramienta, para
   clonar o recuperar cuando Windows no arranca o el equipo no tiene sistema.

## Requisitos funcionales

### Detección e inspección
- RF-01 Listar los discos físicos con modelo, número de serie, capacidad, tipo (HDD/SSD),
  bus (SATA/NVMe/USB), estilo de partición (MBR/GPT) y tamaño de sector lógico y físico.
- RF-02 Mostrar el mapa de particiones de cada disco: tipo, sistema de archivos, tamaño,
  espacio usado, letra, y cuál es la de arranque y cuál la del sistema.
- RF-03 Mostrar el estado de salud del disco (SMART / contadores de fiabilidad) y avisar si
  el origen presenta fallos.
- RF-04 Detectar BitLocker o cifrado de dispositivo en el origen y avisar de sus efectos.

### Clonado
- RF-10 Clonar un disco completo a otro, incluido el disco del sistema **con Windows en uso**.
- RF-11 El clon del disco del sistema debe arrancar sin reparación manual, en UEFI/GPT y en
  BIOS/MBR.
- RF-12 Dos modos de copia: **inteligente** (solo bloques en uso, por defecto) y
  **sector a sector** (copia íntegra, también para sistemas de archivos no reconocidos).
- RF-13 Destino mayor que el origen: ofrecer mantener tamaños, extender la partición
  principal o repartir el espacio proporcionalmente.
- RF-14 Destino menor que el origen: permitirlo si los datos usados caben; si no, impedirlo
  con un mensaje claro.
- RF-15 Clonar una partición individual a espacio libre o a una partición de otro disco.
- RF-16 Alinear las particiones a 1 MiB cuando el destino es SSD.
- RF-17 Conversión MBR↔GPT del destino cuando el equipo de destino lo requiera.
- RF-18 Elegir si el clon conserva los identificadores del disco (clon de sustitución) o
  recibe unos nuevos (para tener ambos discos conectados a la vez), ajustando el arranque.

### Disco con fallos (modo rescate)
- RF-20 Ante un error de lectura: reintentar, reducir el tamaño de bloque hasta aislar el
  sector, rellenar con ceros lo ilegible y continuar.
- RF-21 Registrar el mapa de sectores no recuperados y mostrar el resumen al terminar.
- RF-22 El origen nunca se escribe.

### Ejecución
- RF-30 Progreso con porcentaje, velocidad, tiempo restante y partición en curso.
- RF-31 Cancelar en cualquier momento, dejando el destino marcado como incompleto.
- RF-32 Verificación opcional al terminar, comparando hashes de origen y destino.
- RF-33 Registro (log) detallado de cada operación, exportable.
- RF-34 Acción opcional al terminar: apagar, suspender o nada.

### Migración a Windows 11 y clonación por red (añadidas el 15/11/2026, etapas 11 y 12)
- RF-50 Migrar a Windows 11: instalar un Windows 11 limpio en un disco nuevo y llevar a él los
  datos, programas y ajustes del sistema actual. Opción de instalarlo en equipos que no cumplen
  los requisitos de hardware de Windows 11, avisando de sus consecuencias (actualizaciones y
  soporte).
- RF-51 El proceso puede reiniciar el equipo; debe explicar la duración aproximada (1–2 h) y
  continuar solo tras los reinicios.
- RF-60 Clonación por red: otro equipo recibe el sistema y los datos de este por la red local.
- RF-61 Dos formas de arrancar el receptor: descargándolo con el navegador del equipo de
  destino, o por arranque PXE en un equipo sin sistema operativo.

### Medio de arranque y otro equipo
- RF-40 Crear un USB de arranque WinPE que incluya la herramienta.
- RF-41 Desde ese medio, clonar discos sin Windows en ejecución.
- RF-42 Al llevar el clon a un equipo con hardware distinto: preparar el arranque para el
  modo del equipo nuevo (UEFI/BIOS) y avisar de las limitaciones (controladores de
  almacenamiento, licencia de Windows ligada al equipo original).

## Requisitos de seguridad de datos (no negociables)

- RS-01 El origen se abre siempre en solo lectura.
- RS-02 El destino no puede ser el disco del sistema en uso, el disco desde el que corre la
  app, ni el propio origen.
- RS-03 Antes de escribir se muestra un resumen de origen, destino y de **todo lo que se va
  a borrar en el destino**, y se exige una confirmación explícita.
- RS-04 Los discos se identifican por número de serie y firma/GUID, no solo por su índice;
  se revalida justo antes de escribir por si el índice cambió.
- RS-05 La tabla de particiones del destino se escribe al final, para que un clon
  interrumpido no parezca un disco válido.
- RS-06 Comprobar antes de empezar: capacidad suficiente, tamaños de sector compatibles y
  que ningún volumen del destino esté en uso.
- RS-07 Toda la lógica que decide qué disco se escribe tiene pruebas unitarias.

## Requisitos no funcionales

- Windows 10 (22H2) y Windows 11, x64. Se ejecuta con privilegios de administrador (UAC).
- Sin conexión a internet y sin telemetría.
- Rendimiento limitado por los discos, no por la app: E/S secuencial con búferes de 1–4 MiB
  alineados a sector, lectura y escritura en hilos separados.
- La interfaz no se bloquea durante el clonado.
- Ejecutable firmado e instalador para su distribución (ver «Producto y distribución»).

## Producto y distribución

Requisitos que la versión final debe cumplir; se completan en la etapa 10, salvo lo que ya
está hecho.

- RP-01 **Nombre: ClonDisk.** Es el nombre que ve el usuario en todas partes: título de la
  ventana principal («ClonDisk») y de las demás («Clonar un disco · ClonDisk»), ejecutables
  (`ClonDisk.exe` para la interfaz y `clondisk-cli.exe` para la CLI: van en la misma carpeta,
  así que sus nombres no pueden diferir solo en mayúsculas), propiedades del archivo, carpeta de
  datos (`%LOCALAPPDATA%\ClonDisk`), instalador, accesos directos y desinstalador. Los nombres
  internos de proyectos y espacios de nombres (`Cloner.*`) se mantienen: son identificadores de
  código, no se muestran. **Hecho** desde la etapa 2.
- RP-02 **Icono propio** alusivo a la app: dos unidades de disco superpuestas, la de atrás
  translúcida (el original) y la de delante opaca (el clon), sobre un cuadrado azul
  redondeado. Debe verse en el `.exe`, las ventanas, la barra de tareas, los accesos directos
  y el instalador, y ser legible a 16 px. Archivo: `src/Cloner.App/Assets/ClonDisk.ico`
  (16 a 256 px), ya incrustado en los dos `.exe` y en las ventanas; falta el instalador.
- RP-03 **Instalador `.exe`.** ClonDisk se distribuye como un único instalador
  (`ClonDisk-Setup-<versión>.exe`). En otro equipo basta con ejecutarlo: instala todo lo
  necesario para que la app funcione, sin instalar .NET aparte, sin compilar nada en ese equipo
  y sin conexión a internet. Para ello la app se publica ya compilada y **autocontenida**
  (`dotnet publish -r win-x64 --self-contained`, con el runtime de .NET incluido). El
  instalador pide elevación, copia los archivos en `Program Files\ClonDisk`, crea los accesos
  directos del menú Inicio (y, opcionalmente, del escritorio) y registra el desinstalador en
  «Aplicaciones instaladas».
- RP-04 El instalador y los ejecutables van firmados con un certificado de firma de código.
- **Publicación (17/11/2026).** ClonDisk es **gratis, sin anuncios, sin registro, sin telemetría
  y de código abierto**; los textos públicos deben decirlo y mantenerse al día con cada etapa.
  - **Repositorio:** público en https://github.com/Changli-ai/ClonDisk (cuenta `Changli-ai`),
    rama `main` enlazada con `origin`. Licencia **GPL-3.0** (`LICENSE`, texto oficial; elegida
    por el usuario frente a MIT y Apache). `README.md` en español: qué es, usos, características,
    seguridad, requisitos, uso de la interfaz y de la CLI, compilación, hoja de ruta, limitaciones
    y enlaces a la web y a la descarga. El usuario también edita el README desde GitHub: antes de
    tocarlo, `git fetch` y fusionar. Temas del repositorio: `clonar-discos`, `clonacion-de-discos`,
    `clonar-disco-duro`, `migrar-windows-a-ssd`, `disk-cloning`, `disk-clone`, `ssd`, `hdd`,
    `windows`, `windows-11`, `csharp`, `dotnet`, `wpf`; página principal: la web.
  - **Releases** (`.zip` con `ClonDisk.exe`, `clondisk-cli.exe` y `LÉEME.txt`: publicación
    autocontenida en un solo archivo y sin firmar; notas en español con la huella SHA-256). Cada
    Release lleva además una copia idéntica del .zip con **nombre fijo, `ClonDisk-win-x64.zip`**
    (desde la v0.1.3, 19/11/2026): los botones «Descargar» de la web, de la raíz del dominio y el
    `downloadUrl` de los datos estructurados apuntan a
    `releases/latest/download/ClonDisk-win-x64.zip`, que descarga directamente el .zip de la última
    versión (GitHub lo sirve como `attachment`); «Todas las versiones» sigue yendo a `releases`.
    - **v0.1.0** (17/11/2026): primera versión pública.
    - **v0.1.1** (18/11/2026,
      https://github.com/Changli-ai/ClonDisk/releases/tag/v0.1.1): identificadores provisionales
      durante la copia, tabla definitiva con el destino sin conexión y diagnóstico de la
      verificación. Commit `5871528`; los dos `.exe` llevan `0.1.1+5871528…` en su versión de
      producto. SHA-256 de `ClonDisk-0.1.1-win-x64.zip`:
      `E437B102135DF7CF7325FF45EDCE78773CFA78CFA1ECC315EFD8852D47499AEA`.
    - **v0.1.2** (18/11/2026): el destino se queda sin conexión durante la copia y la
      verificación, y el diagnóstico apunta a un montaje del clon si falla fuera de la tabla.
    - **v0.1.3** (19/11/2026, la última,
      https://github.com/Changli-ai/ClonDisk/releases/tag/v0.1.3): aviso destacado al terminar un
      clon que se deja sin conexión y «Reparar el arranque» (interfaz y CLI `repair-boot`),
      probado por el usuario en un clon real que no arrancaba y que, reparado, arrancó. Commit
      `8d23d77`; SHA-256 de `ClonDisk-0.1.3-win-x64.zip`:
      `4AF43383E6EAE78C41A493E17D1F73955381ECDB246677BF45EA5A005EFE8522`.
  - **Web** en GitHub Pages desde `docs/` (rama `main`): https://changli-ai.github.io/ClonDisk/.
    Una sola página en español (`index.html`, CSS propio, modo oscuro, adaptable al móvil) pensada
    para búsquedas como «clonar discos», «clonar disco duro a SSD» y «migrar Windows a SSD»: datos
    estructurados `SoftwareApplication` y `FAQPage`, Open Graph, `robots.txt`, `sitemap.xml` y
    `.nojekyll`. Imágenes en `docs/assets`: logotipo y favicon sacados de `ClonDisk.ico`, y
    capturas del proyecto de dibujo a PNG de la interfaz (las del asistente, recortadas porque
    salen con el título cortado).
  - **Google Search Console:** propiedad `https://changli-ai.github.io/ClonDisk/` verificada con
    la etiqueta `google-site-verification` de `docs/index.html` (no quitarla); sitemap enviado e
    indexación solicitada el 17/11/2026. El sitemap salía «No se ha podido obtener», lo normal en
    una propiedad recién creada. Pendiente: confirmar que la página queda indexada
    (`site:changli-ai.github.io/ClonDisk`).
  - **Raíz del dominio (19/11/2026):** Google ya mostraba la web en sus resultados, pero con el
    icono genérico y el nombre de sitio «GitHub Pages documentation», porque los toma de la raíz
    `https://changli-ai.github.io/`, que no existía. Se creó el repositorio público
    https://github.com/Changli-ai/changli-ai.github.io (GitHub Pages en la raíz, rama `main`) con
    una página de inicio que presenta ClonDisk y enlaza a `/ClonDisk/`: favicon (`favicon.ico` y
    PNG de 48, 96 y 192 px sacados del fotograma de 256 px de `ClonDisk.ico`), datos
    estructurados `WebSite` con el nombre «ClonDisk», `site.webmanifest`, `robots.txt` (que vale
    para todo el dominio) y `sitemap.xml` con la raíz y `/ClonDisk/`. La propiedad
    `https://changli-ai.github.io/` está verificada en Search Console (la misma etiqueta
    `google-site-verification` que la web de ClonDisk, en el `index.html` de la raíz; no quitarla)
    y su sitemap enviado (19/11/2026). Google tarda días o semanas en cambiar icono y nombre;
    pendiente comprobarlo en los resultados.
  - **Herramientas:** GitHub CLI 2.104.0 instalado con winget en `C:\Program Files\GitHub CLI\gh.exe`
    (no está en el PATH de las consolas de desarrollo; winget está en
    `%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe`), con sesión iniciada como `Changli-ai`.
  - **Al publicar una versión nueva** (así se hizo la 0.1.1): subir `Version` en
    `Directory.Build.props` y la versión en el README, en la web (`softwareVersion`, la nota bajo
    los botones y la tabla de descarga) y en `LÉEME.txt`, y la fecha de `sitemap.xml`; confirmar y
    subir **antes** de publicar, para que los `.exe` lleven el commit correcto en su versión;
    publicar autocontenido en `publish/ClonDisk-<versión>-win-x64/`, comprimirlo y crear la Release
    `v<versión>` con `gh release create … --latest` y notas en español con la huella SHA-256,
    subiendo **los dos archivos**: `ClonDisk-<versión>-win-x64.zip` y su copia
    `ClonDisk-win-x64.zip` (sin ella, la descarga directa de la web daría error 404);
    comprobar que la web muestra la versión nueva y que la descarga funciona.
- RP-05 Se comprueba en un Windows 10 y un Windows 11 limpios, sin .NET ni herramientas de
  desarrollo: instalar, abrir, clonar entre VHD y desinstalar sin dejar restos.

## Diseño de la interfaz (15/11/2026)

El usuario mostró cómo quiere que se vea el producto final. Se toma su **estructura**, con la
identidad visual propia de ClonDisk (colores, iconos, ilustraciones y textos propios, según las
reglas de trabajo):

- Barra lateral a la izquierda, en azul, con los modos: Disco, Sistema y Partición; separador;
  Migrar a Windows 11, Disco de emergencia y Clonación por red. Los que aún no existen se
  muestran como «Próximamente».
- Cada modo ocupa el área derecha: título, una frase de explicación y dos tarjetas grandes
  —origen y destino— unidas por una flecha. Vacías, tienen borde discontinuo, fondo claro y un
  botón «+» (azul para el origen, verde para el destino); al elegir, muestran el disco o el
  sistema elegido y una «×» para quitarlo. Botón «Siguiente» abajo a la derecha, desactivado
  hasta completar la selección.
- Modo Sistema: el origen ya viene elegido (el Windows en uso y sus particiones); solo se elige
  el destino.
- Disco de emergencia: elegir USB, CD/DVD o crear una ISO (ruta y «Examinar…»), botón
  «Añadir controlador» y «Crear».
- Clonación por red: elegir entre receptor por navegador o arranque PXE, con guía.

## Tecnologías

| Área | Elección |
|---|---|
| Lenguaje y runtime | C# sobre **.NET 10 (LTS)**. SDK instalado: 10.0.103 |
| Interfaz | **WPF** con MVVM (`CommunityToolkit.Mvvm`) |
| Llamadas a Win32 | P/Invoke generado con `Microsoft.Windows.CsWin32` |
| Acceso crudo a disco | `CreateFile` sobre `\\.\PhysicalDriveN` y `\\.\Volume{GUID}`, con `FILE_FLAG_NO_BUFFERING` |
| Control de disco y volumen | `DeviceIoControl`: `IOCTL_DISK_GET_DRIVE_GEOMETRY_EX`, `IOCTL_DISK_GET/SET_DRIVE_LAYOUT_EX`, `IOCTL_DISK_CREATE_DISK`, `IOCTL_DISK_UPDATE_PROPERTIES`, `IOCTL_STORAGE_QUERY_PROPERTY`, `FSCTL_LOCK_VOLUME`, `FSCTL_DISMOUNT_VOLUME` |
| Bloques en uso (NTFS) | `FSCTL_GET_VOLUME_BITMAP`, `FSCTL_GET_NTFS_VOLUME_DATA` |
| Enumeración y salud | Storage Management API por WMI (`MSFT_Disk`, `MSFT_Partition`, `MSFT_PhysicalDisk`, `MSFT_StorageReliabilityCounter`) |
| Instantánea en caliente | **VSS** desde C# con la interfaz COM `IVssBackupComponents` generada por CsWin32 (sin AlphaVSS ni DLL nativa): contexto de copia de seguridad con escritores, tipo COPY, instantáneas no persistentes |
| Arranque | `bcdboot` y edición del BCD; partición EFI en GPT, sector de arranque en MBR |
| Medio de arranque | WinPE, construido a partir de Windows ADK o del entorno de recuperación local (WinRE) |
| Verificación | XxHash3 por bloque de 4 MiB y XxHash128 de todo lo escrito (`System.IO.Hashing`) |
| Registro | `Microsoft.Extensions.Logging` con salida a archivo |
| Pruebas | xUnit; integración contra discos virtuales VHD/VHDX |
| Distribución | Publicación autocontenida `win-x64`, instalador `.exe` (herramienta por decidir en la etapa 10; candidata: Inno Setup), manifiesto `requireAdministrator`, firma de código |

## Arquitectura

El motor no depende de la interfaz: debe poder ejecutarse desde la CLI y dentro de WinPE.

```
src/
  Cloner.Core/    Motor: enumeración, E/S cruda, tablas de particiones, VSS,
                  planificación del clonado, copia, verificación. Sin UI.
  Cloner.Cli/     Línea de comandos sobre Core. Para pruebas, automatización y WinPE.
  Cloner.App/     Interfaz WPF sobre Core.
tests/
  Cloner.Core.Tests/         Unitarias (sin tocar discos).
  Cloner.IntegrationTests/   Contra VHD/VHDX; requieren administrador.
```

Inventario de discos (`Cloner.Core/Inventory`, etapa 1):

- `WmiStorageReader` consulta WMI y devuelve registros crudos (`StorageSnapshot`);
  `DiskInventoryBuilder` los interpreta y produce el modelo público (`DiskInfo`,
  `PartitionInfo`). La interpretación se prueba con registros falsos, sin tocar discos.
- `IDiskInventory` es el punto de entrada para la CLI y la interfaz.
- Sin administrador, `DiskInfo.Reliability` es nulo y el cifrado queda en `Unknown`. La
  consulta de BitLocker se omite sin privilegios porque tarda unos 5 s en ser denegada.
- `InventoryText` concentra los textos en español que comparten la CLI y la interfaz.

Interfaz (`Cloner.App`): MVVM con `MainViewModel` → `DiskViewModel`. `MainWindow` acepta un
`MainViewModel` por constructor. Como la app pide elevación, para revisar la interfaz sin UAC
se usa un proyecto temporal fuera del repositorio que referencia `Cloner.App`, crea la
ventana con un `IDiskInventory` falso y la dibuja a PNG con `RenderTargetBitmap`.

Un clonado se separa en dos fases: **planificar** (produce un plan inmutable y revisable con
todo lo que se leerá y escribirá) y **ejecutar** (aplica el plan). La interfaz muestra el plan
para la confirmación de RS-03, y las pruebas validan planes sin escribir nada.

Clonado (`Cloner.Core/Cloning`, etapa 2):

- `ICloneService` (`CloneService`) es la entrada para la CLI y la interfaz: `CreatePlan` lee la
  tabla del origen y llama a `ClonePlanner`; `RunAsync` revalida con un inventario nuevo
  (`PlanValidation`, RS-04) y ejecuta `CloneExecutor`. `RunAsync` no lanza: todo fallo va en
  `CloneResult`.
- `ClonePlanner` concentra las reglas RS-02/RS-06 y el orden de escritura. Orden (RS-05): borrar
  el primer MiB y el último MiB del destino, copiar los datos, escribir la GPT de respaldo y,
  por último, el primer MiB con la tabla. Un clon interrumpido queda sin tabla (disco «sin
  inicializar»). Si el destino es mayor y el origen es GPT, la tabla se ajusta: la cabecera
  apunta a una copia de respaldo nueva al final del destino (`PartitionTable`).
- `CloneExecutor` usa un hilo lector y otro escritor con 4 búferes alineados de 4 MiB. Calcula
  XxHash3 de cada bloque escrito y XxHash128 de todo; la verificación relee el destino y compara
  bloque a bloque. Si no coincide, vuelve a borrar la tabla. Antes de escribir comprueba que el
  dispositivo abierto y su tabla son los del plan, bloquea los volúmenes del origen y bloquea y
  desmonta los del destino.
- Clonado en caliente y copia inteligente (etapa 3):
  - El plan tiene un paso `CopyPartition` por partición. Los volúmenes NTFS/ReFS montados de un
    disco que no es de solo lectura se leen de una **instantánea VSS** (`FromSnapshot`), tomada
    a la vez para todos (`Snapshots/VssSnapshotSet`); no se bloquean. El resto de volúmenes del
    origen se bloquean; si la partición EFI no se deja bloquear, se copia igualmente (Windows no
    la modifica en uso normal).
  - Copia inteligente (`CloneMode.UsedBlocks`, por defecto): de los volúmenes NTFS solo se
    copian los clústeres en uso según `FSCTL_GET_VOLUME_BITMAP` (`NtfsUsage`, huecos de menos de
    1 MiB incluidos) más el final del volumen (copia del sector de arranque); el espacio sin
    particionar no se copia, salvo dentro de una partición extendida (tablas EBR). Sector a
    sector (CLI `--sector`) lo copia todo.
  - El disco del sistema solo puede ser origen si hay instantáneas (no en WinPE:
    `Elevation.IsWinPE`). Sigue sin poder ser destino.
  - El ejecutor convierte los pasos en tramos (`CopyRange`) con su dispositivo de origen antes
    de copiar; las instantáneas se liberan al terminar la copia, antes de verificar.
  - Leído desde una instantánea, un volumen con BitLocker queda sin cifrar en el clon (aviso).
- Distribución e identificadores (`Cloner.Core/Layout`, etapa 4):
  - `LayoutPlanner` decide dónde va cada partición: alineación a 1 MiB si el destino es SSD o se
    pide (`AlignmentMode`, RF-16) y uso del espacio sobrante (`SizeMode`: mantener, ampliar la
    principal —la de Windows o la NTFS de datos mayor— o repartir entre las NTFS de datos,
    RF-13). Las que van detrás de una que crece se desplazan (la de recuperación queda al
    final). No mueve particiones lógicas MBR.
  - Si la distribución cambia o se piden identificadores nuevos (RF-18), el plan es de «tabla
    reconstruida»: cada partición se copia entera a su posición nueva (`CloneStep.SourceOffset`)
    y `TableBuilder` genera la tabla a partir de las entradas reales del origen (mismos tipos,
    atributos y nombres). Al mover una partición se actualiza el campo de sectores ocultos de su
    sector de arranque NTFS/FAT32 (`BootSector`).
  - Remate con el clon montado (`ITargetFinisher`, `Devices/TargetFinisher`): ampliar los NTFS
    que crecen con `FSCTL_EXTEND_VOLUME`; si el clon es de Windows y cambian sus identificadores
    (o se mueven particiones en MBR), traducir `MountedDevices` en su registro (`RegLoadKey`) y
    rehacer el BCD con `bcdboot`. Si el clon debe conservar los identificadores del origen,
    se copia con unos provisionales (con los del origen Windows lo dejaría sin conexión), se
    hace el remate, se esperan 90 s (volsnap) y se escribe la tabla definitiva.
  - La reparación del arranque con identificadores nuevos no está probada arrancando un clon:
    queda para la prueba manual del usuario.
- Destino más pequeño y conversión (etapa 5):
  - `LayoutPlanner` acepta un destino menor si las particiones caben (se rehace la tabla) o si
    reduciendo las NTFS de datos con más espacio libre, sin bajar de lo usado + 10 % (mínimo
    16 MiB, `MinimumSize`), caben; si no, lo impide diciendo el tamaño necesario. Sector a
    sector no puede reducir.
  - Una partición reducida es un paso `CopyFiles`: se borra su primer MiB, se copia el resto
    por bloques, se monta el clon con la tabla provisional, se formatea en NTFS (WMI
    `MSFT_Volume.Format`, etiqueta «ClonDisk incompleto») y se llena con `FileCopy/FileTreeCopier`
    desde la instantánea (que se mantiene hasta entonces). Al final recibe la etiqueta del origen.
    Si falla o se cancela, se desmontan los volúmenes del clon y se borra la tabla.
  - `FileTreeCopier`: BackupRead/BackupWrite (datos, flujos alternativos, seguridad, datos de
    enlace), enlaces duros por identificador de archivo, nombres cortos, atributos y fechas
    (las de las carpetas al final). Excluye lo de `CopyExclusions` (paginación, hibernación,
    `System Volume Information` y, si el origen es el disco del sistema, `FilesNotToBackup`).
    Los archivos EFS no se copian; los comprimidos con WOF salen descomprimidos. Un hilo recorre
    y crea las carpetas y 8 copian archivos a la vez (con archivos pequeños el tiempo se va en
    abrir y crear, no en transferir): ×2 medido en el SSD (`FileCopyBenchmark`, con
    `CLONDISK_BENCH=1`). Si el volumen de origen o destino desaparece, se detiene en vez de
    acumular errores. Un fallo al poner la etiqueta solo se anota.
  - Mientras se termina el clon, la tabla provisional marca todas las particiones GPT «sin letra de
    unidad» (en MBR se quitan las letras al aparecer): así Windows no ofrece formatear la partición
    aún vacía, que fue lo que estropeó la primera prueba real (17/11/2026). La pausa de seguridad
    de 90 s se muestra con cuenta atrás, también al cancelar.
  - **Prueba real del 17/11/2026** (disco 0, el SSD del sistema, a un HDD USB de 931 GB, con el
    permiso del usuario; el destino es menor, así que C: se reduce y se copia archivo a archivo):
    - Primer intento: Windows dio letra a la C: aún vacía del clon y el Explorador ofreció
      formatearla; la copia se rompió («El dispositivo no está listo», acceso denegado) y falló al
      poner la etiqueta. Se corrigió con la tabla provisional sin letras, la parada si el volumen
      desaparece y la etiqueta como fallo no grave. Cancelar parecía colgar la ventana: era la
      pausa de 90 s sin avisos (ahora con cuenta atrás).
    - Con un solo hilo, la copia de C: iba a ~0,6 MB/s (estimación de unas 290 h). Con 8 hilos,
      entre 12 y 42 MB/s según el tamaño de los archivos (media de ~20 MB/s, unas 4 h para 353 GB).
      El usuario canceló a las 1 h 31 min (~30 %), sin ningún archivo con error; la cancelación
      dejó el disco sin tabla, como debe.
    - Límites medidos: el HDD estaba conectado a velocidad USB 2.0 (puerto `HS03` del
      controlador xHCI, controlador `USBSTOR`, sin UASP), que topa en ~35–40 MB/s; y Windows
      Defender usaba ~40 % de CPU analizando cada archivo nuevo. Al usuario se le explicó cómo
      excluir el proceso `ClonDisk.exe` en Seguridad de Windows mientras dura el clonado (no lo
      cambia la app ni el asistente). Con un SSD igual o mayor conectado por dentro o por USB 3.0
      se usaría la copia por bloques, de minutos en lugar de horas.
    - Al cancelar el clonado de la CLI con Ctrl+C dentro de un script de PowerShell lanzado con
      `Start-Process -Verb RunAs`, se cerró toda la ventana y se mató el proceso a mitad de la
      cancelación (las instantáneas quedaron hasta el reinicio y el disco con la tabla
      provisional). Para clonados reales hay que lanzar la interfaz, o la CLI directamente en una
      consola de administrador.
  - **Prueba real del 18/11/2026** (otro equipo, Windows 10 con AVG AntiVirus; disco del sistema,
    un WD de 250 GB GPT, a otro idéntico): 54,7 GB copiados en 17 min, pero la verificación falló
    «a partir del byte 0». Todos los bloques coincidían salvo el primer MiB, con la tabla. Causa
    probable: la tabla se escribía con los identificadores del disco del sistema en uso y, al
    aparecer un disco duplicado, Windows (o la protección del MBR de AVG) la cambió; en las pruebas
    del 17/11, con identificadores provisionales, la misma verificación pasaba.
    Arreglo: si el clon conserva los identificadores del origen, la copia escribe siempre la tabla
    con unos provisionales (`TableBuilder.ApplyIdentity` sobre la tabla copiada, o la tabla
    reconstruida con ellos) y, después de verificar, `WriteFinalTable` pone el destino **sin
    conexión** (`IOCTL_DISK_SET_DISK_ATTRIBUTES`, sin guardar: vuelve en línea al reiniciar o
    reconectarlo) y escribe la tabla definitiva; si no se puede escribir sin conexión, la escribe en
    línea y quita la conexión al acabar. A los 5 s relee la tabla y, si cambió, lo dice en el
    resultado. El clon se deja sin conexión y no se pide a Windows que relea su tabla; el resultado
    pide apagar antes de cambiar los discos. La huella de lo copiado se calcula con la tabla
    definitiva. Diagnóstico (`VerifyDiagnosis`): si la verificación falla, se anotan todos los
    bloques distintos y, del primer MiB, qué bytes cambiaron (código de arranque, firma o GUID del
    disco, entradas GPT…) con una explicación: identificadores cambiados (Windows con discos
    duplicados), principio vacío o código de arranque cambiado (antivirus). Probado con 188 pruebas
    unitarias (incluidas las que simulan a Windows cambiando el GUID de un disco duplicado y a un
    antivirus vaciando el MBR) y las 9 de `VhdCloneTests` como administrador: Windows deja poner un
    VHD sin conexión y escribir en él, y el clon de igual tamaño queda sin conexión con la tabla
    idéntica a la del origen. Publicado en la versión 0.1.1.
    - **Segundo intento (18/11/2026, versión 0.1.1):** el principio del disco ya coincidía, pero
      fallaron 15 bloques desde el byte 122.683.392, el inicio de C: en el clon. Con la tabla
      provisional (identificadores nuevos, sin choque con el original) Windows montaba la C: del
      clon durante la verificación (17 min en un disco real; en un VHD dura menos de un segundo, por
      eso las pruebas no lo veían) y escribía metadatos NTFS en ella. Arreglo (versión 0.1.2): el
      destino se pone **sin conexión justo después de bloquear sus volúmenes** y sigue así durante la
      copia y la verificación; vuelve a estar en línea al terminar si el clon tiene identificadores
      nuevos o hay que montarlo para rematarlo, y si el clonado falla o se cancela. Con los
      identificadores del origen se queda sin conexión. 190 pruebas unitarias (una simula a Windows
      escribiendo en el clon montado).
    - **Tercer intento (18/11/2026, versión 0.1.2):** el clonado se completó y se verificó, y el
      clon quedó sin conexión. Después el usuario lo puso en línea en Administración de discos con
      el original conectado: Windows le cambió los identificadores para resolver el choque y, al
      arrancar desde él, salió el error 0xc000000e (su BCD buscaba la partición de Windows por los
      identificadores antiguos). Respuesta (versión 0.1.3):
      - Aviso destacado al terminar (`CloneResult.TargetLeftOffline`, textos en
        `CloneText.OfflineNoticeTitle`/`OfflineNoticeSteps`): recuadro «Importante: no pongas el
        clon en línea en este equipo» con los pasos (apagar, cambiar los discos, encender y, si no
        arranca, reparar). La CLI lo imprime igual.
      - **Reparar el arranque** (`Cloning/BootRepairService`, `BootRepairer`): botón «Reparar el
        arranque…» en «Discos del equipo» (solo en discos que pueden tener un Windows: no el del
        sistema ni el de la app, con tabla, partición de datos y, en GPT, partición EFI) y
        `clondisk-cli repair-boot <disco>` (pide escribir REPARAR). Pone el disco en línea si hace
        falta, espera sus volúmenes, busca su Windows, lee **ya en línea** la identidad actual de
        esa partición, traduce en su registro el valor de `\DosDevices\C:` a esa identidad
        (`ITargetFinisher.RepairBootInPlace`) y ejecuta `bcdboot` en modo UEFI (EFI) o BIOS
        (partición activa). Registro propio («reparacion-…»). Probado con dobles (196 pruebas
        unitarias).
      - **Primera prueba real (19/11/2026):** falló con «No hay ninguna letra de unidad libre para
        montar el clon», un mensaje engañoso: había letras libres, pero `SetVolumeMountPoint` no
        dejaba dar letra a la partición EFI del clon. `MountWithLetter` (en `TargetFinisher`) usa
        ahora, por orden: la letra que ya tenga el volumen (sin quitarla después), una libre con
        `SetVolumeMountPoint` y, si Windows no lo deja, `diskpart` («select disk / select partition
        / assign letter», buscando disco y partición en el inventario), quitándola al terminar; si
        todo falla, el mensaje da el error real de Windows. Prueba de integración nueva,
        `MountLetterTests` (VHD con partición EFI FAT32 y otra NTFS, `VirtualDisk.CreateWithEfi`):
        ambas reciben una letra temporal y se les quita. **Segunda prueba real:** el arranque se
        reparó y el clon arrancó Windows con los archivos del original.
  - Conversión (`StyleConversion`, `CloneOptions.TargetStyle`): siempre con identificadores
    nuevos y particiones alineadas a 1 MiB. A MBR: como mucho 4 particiones, se descarta la MSR,
    el código de arranque es propio (`PartitionTables/MbrBootCode`: busca la partición activa, lee
    su primer sector con INT 13h extendida y salta a él; `IOCTL_DISK_CREATE_DISK` no escribe código
    de arranque, se comprobó) y la partición de Windows queda activa. Ese código solo se probará
    arrancando un clon convertido. A GPT: si un Windows no tiene partición EFI, se crean
    una EFI de 100 MiB y una MSR de 16 MiB al principio; la EFI figura como de datos en la tabla
    provisional para poder formatearla en FAT32, y la tabla definitiva se escribe al final.
    `MountedDevices.Map` traduce entre las dos formas de identidad y `bcdboot` rehace el arranque
    en el modo nuevo (UEFI o BIOS).
- `IDiskAccess`/`IBlockDevice` se sustituyen en las pruebas por discos en memoria
  (`MemoryDisk`) o archivos (`DiskDevice.OpenFile`), que recorren la misma E/S sin caché.
- `Devices/DiskDevice` usa CsWin32 solo para funciones y constantes; las estructuras de los
  IOCTL se interpretan a mano en `DeviceDescriptors`, con pruebas.
- Registro: un archivo por operación en `%LOCALAPPDATA%\ClonDisk\Registros` (`FileLogger`, con
  mensajes `LoggerMessage` en `CloneLog`). La interfaz permite exportarlo.
- Por defecto el clon conserva el identificador del disco: con origen y clon conectados a la
  vez, Windows deja uno sin conexión. Con identificadores nuevos (etapa 4, RF-18) pueden
  convivir.
- `CloneOptions.VirtualTargetOnly` (CLI: `--virtual-only`) solo admite destinos VHD/VHDX; las
  pruebas de integración y cualquier prueba manual durante el desarrollo deben usarlo.

Interfaz (rediseñada el 15/11/2026, ver «Diseño de la interfaz»): `MainWindow` tiene una barra
lateral (`NavItem`, `AppSection`) y una sección a la derecha, elegida con
`SectionVisibilityConverter`. Disco y Sistema usan `CopyPanelViewModel`: tarjetas de origen y
destino (`CardContent`), selector `DiskPickerWindow` y «Siguiente», que abre `CloneWindow`
(`CloneViewModel`) con ambos discos ya elegidos. El asistente tiene cuatro fases: opciones (modo
de copia, uso del espacio sobrante e identificadores nuevos), revisión del plan con lo que se
borrará y la distribución en el destino, progreso con cancelación y resultado con exportación
del registro. Partición, Migrar a Windows 11, Disco de emergencia y Clonación por red muestran
su pantalla desactivada con «Próximamente». «Discos del equipo» es la vista de inspección de
la etapa 1. Los iconos son glifos de Segoe MDL2 Assets. Ojo: un `ContentPresenter` con
`Content` explícito toma ese contenido como su propio `DataContext`; para tarjetas con
plantilla se usa `ContentControl`.

Arranque de la interfaz: `App.OnStartup` (sin `StartupUri`) muestra `SplashWindow`, la pantalla
de carga (logotipo, nombre, barra indeterminada, estado y versión), mientras lee los discos con
el `MainViewModel`; se ve al menos 1,5 s y después abre `MainWindow` con los datos ya cargados
(`MainViewModel.HasLoaded` evita leerlos dos veces).

## Etapas

| # | Etapa | Requisitos | Resultado comprobable |
|---|---|---|---|
| 0 | Preparación | — | Solución, proyectos, git y pruebas ejecutándose |
| 1 | Inspección (solo lectura) | RF-01 a RF-04 | La app y la CLI listan discos y particiones reales |
| 2 | Clonado sector a sector de un disco de datos, destino ≥ origen | RF-12 (sector), RF-30 a RF-33, RS-01 a RS-07 | VHD clonado e idéntico por hash |
| 3 | Disco del sistema en caliente, clon idéntico arrancable | RF-10, RF-11 | El clon arranca en una máquina virtual y en hardware real |
| 4 | Ajuste de particiones (la copia inteligente se adelantó a la etapa 3) | RF-13, RF-16, RF-18 | Clon a disco mayor con la partición extendida |
| 5 | Destino más pequeño y conversión MBR↔GPT | RF-14, RF-17 | HDD grande clonado a SSD menor que arranca |
| 6 | Clonado de partición individual | RF-15 | Partición copiada a una unidad externa |
| 7 | Modo rescate para discos con fallos | RF-20 a RF-22 | Clon completado con errores de lectura simulados |
| 8 | Medio de arranque WinPE | RF-40, RF-41 | USB que arranca y clona |
| 9 | Clon para otro equipo | RF-42 | Clon que arranca en hardware distinto |
| 10 | Distribución | RP-01 a RP-05 | `ClonDisk-Setup.exe` firmado que instala y ejecuta la app en un Windows sin .NET |
| 11 | Migración a Windows 11 | RF-50, RF-51 | Equipo con Windows 10 migrado a un Windows 11 nuevo con sus datos y programas |
| 12 | Clonación por red | RF-60, RF-61 | Equipo de destino que recibe el clon por red, desde el navegador y por PXE |

## Riesgos y decisiones abiertas

- **VSS desde .NET.** Interfaz COM generada por CsWin32, validada en ejecución con
  `SnapshotTests` y `VhdCloneTests`. El dispositivo de una instantánea declara la longitud de la
  partición pero no deja leer lo que queda tras los clústeres de NTFS (copia del sector de
  arranque): `DiskDevice.ReadableLength` lo mide y ese final se lee del disco.
- **WPF dentro de WinPE.** No está garantizado que una app WPF de .NET 10 funcione en WinPE;
  por eso el motor y la CLI son independientes. Validar al empezar la etapa 8.
- **Sectores 512 frente a 4K nativo.** Hasta nuevo aviso, origen y destino deben tener el
  mismo tamaño de sector lógico; si no, se rechaza el clonado.
- **Destino más pequeño (etapa 5).** Resuelto copiando archivo a archivo las particiones
  reducidas. Riesgos: un Windows con CompactOS ocupa más descomprimido y los archivos con
  compresión NTFS salen sin comprimir, así que el margen sobre lo usado puede quedarse corto.
- **Cifrado.** Leído desde la instantánea, el contenido sale descifrado; el clon queda sin
  cifrar y hay que reactivar el cifrado después.
- **Hardware distinto (etapa 9).** El arranque depende de los controladores de almacenamiento
  del equipo nuevo y la licencia de Windows puede no transferirse. La app puede preparar y
  avisar, no garantizar.
- **Imagen a archivo (VHDX).** No está en el alcance; valorar si se añade como alternativa al
  clon disco a disco para copias de seguridad.
- Pendiente: herramienta del instalador y certificado de firma de código (etapa 10).
- **Pendiente, pedido por el usuario (17/11/2026): reanudar un clonado cancelado o interrumpido.**
  Hoy, al cancelar, el destino se queda sin tabla y el siguiente clonado empieza desde cero, aunque
  la copia archivo a archivo de C: lleve horas (en la prueba real, ~20 MB/s a un HDD por USB 2.0).
  Idea: conservar la partición reducida a medias (marcada «ClonDisk incompleto») y, al repetir el
  clonado con el mismo origen y destino, saltarse los archivos que ya coincidan en tamaño y fechas;
  rehacer siempre la parte por bloques, la tabla y el remate. Hay que decidir cómo se identifica un
  destino reanudable sin debilitar RS-05 (que un clon incompleto no parezca válido).

## Comandos

La solución es `Cloner.slnx`. Desde la raíz del proyecto:

```
dotnet build                                 # compila todo; las advertencias son errores
dotnet test                                  # todas las pruebas
dotnet test tests/Cloner.Core.Tests          # solo unitarias
dotnet run --project src/Cloner.Cli -- list  # CLI: discos y particiones (solo lectura)
dotnet run --project src/Cloner.Cli -- plan 1 2                   # plan de clonado, sin escribir
dotnet run --project src/Cloner.Cli -- clone 1 2 --virtual-only   # clona; pide escribir BORRAR
                                             # --sector: sector a sector; --no-verify: sin verificar
                                             # --extend | --proportional; --align | --no-align; --new-ids; --gpt | --mbr
                                             # sin consola: cmd /c "echo BORRAR| clondisk-cli clone …"
dotnet run --project src/Cloner.App          # interfaz; pide elevación (UAC)
```

- `plan` y `clone` requieren administrador. Códigos de salida de `clone`: 0 correcto,
  1 argumentos, 3 bloqueado por las reglas, 4 cancelado, 5 fallo, 6 verificación fallida.
- `VhdCloneTests` crea VHD fijos con `diskpart` en `%TEMP%\cloner-vhd`, los clona y los borra.
  Al terminar un clonado, Windows relee la tabla, monta el volumen del clon y escribe metadatos
  NTFS; y al desmontar un origen montado en escritura, también escribe en él. Por eso la prueba
  de igualdad monta el origen en solo lectura y demuestra la identidad por hash en el momento de
  terminar (huella de lo escrito = huella del origen, y verificación del destino), no
  comparando los archivos VHD enteros después.
- Las sesiones de desarrollo no son de administrador. Para las pruebas con VHD se lanza
  `dotnet test tests/Cloner.IntegrationTests --no-build` en un PowerShell elevado
  (`Start-Process -Verb RunAs`), con permiso del usuario, que acepta el aviso de UAC.

- Las pruebas de integración marcadas con `[AdminFact]` se omiten si la consola no es de
  administrador. Úsalo en toda prueba que monte o escriba discos virtuales.
- `Directory.Build.props` fija la versión y las opciones comunes (`Nullable`,
  `TreatWarningsAsErrors`, analizadores). Todos los proyectos apuntan a `net10.0-windows`.
- `Cloner.App` lleva un manifiesto `requireAdministrator`; `Cloner.Cli` no, y comprueba los
  privilegios en ejecución con `Elevation.IsAdministrator()`.
