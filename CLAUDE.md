# App de clonación de discos (nombre provisional)

Aplicación de escritorio para Windows 10 y 11 que clona discos y particiones entre HDD y SSD
(HDD↔SSD, HDD↔HDD, SSD↔SSD; SATA, M.2 y NVMe, internos o por USB), incluido el disco del
sistema: al retirar el disco original y poner el clon, el equipo debe arrancar y funcionar
exactamente igual.

**Estado actual:** etapas 0 y 1 completadas. La app y la CLI inspeccionan discos y particiones
en solo lectura; todavía no hay ninguna función de clonado. La siguiente es la etapa 2.

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
- Ejecutable firmado e instalador para su distribución.

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
| Instantánea en caliente | **VSS** (Volume Shadow Copy Service). Vía por decidir en la etapa 3: AlphaVSS, WMI `Win32_ShadowCopy` o un shim nativo en C++ |
| Arranque | `bcdboot` y edición del BCD; partición EFI en GPT, sector de arranque en MBR |
| Medio de arranque | WinPE, construido a partir de Windows ADK o del entorno de recuperación local (WinRE) |
| Verificación | SHA-256 o xxHash por bloques |
| Registro | `Microsoft.Extensions.Logging` con salida a archivo |
| Pruebas | xUnit; integración contra discos virtuales VHD/VHDX |
| Distribución | Manifiesto `requireAdministrator`, firma de código, instalador (por decidir) |

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

## Etapas

| # | Etapa | Requisitos | Resultado comprobable |
|---|---|---|---|
| 0 | Preparación | — | Solución, proyectos, git y pruebas ejecutándose |
| 1 | Inspección (solo lectura) | RF-01 a RF-04 | La app y la CLI listan discos y particiones reales |
| 2 | Clonado sector a sector de un disco de datos, destino ≥ origen | RF-12 (sector), RF-30 a RF-33, RS-01 a RS-07 | VHD clonado e idéntico por hash |
| 3 | Disco del sistema en caliente, clon idéntico arrancable | RF-10, RF-11 | El clon arranca en una máquina virtual y en hardware real |
| 4 | Copia inteligente y ajuste de particiones | RF-12 (inteligente), RF-13, RF-16, RF-18 | Clon a disco mayor con la partición extendida |
| 5 | Destino más pequeño y conversión MBR↔GPT | RF-14, RF-17 | HDD grande clonado a SSD menor que arranca |
| 6 | Clonado de partición individual | RF-15 | Partición copiada a una unidad externa |
| 7 | Modo rescate para discos con fallos | RF-20 a RF-22 | Clon completado con errores de lectura simulados |
| 8 | Medio de arranque WinPE | RF-40, RF-41 | USB que arranca y clona |
| 9 | Clon para otro equipo | RF-42 | Clon que arranca en hardware distinto |
| 10 | Distribución | — | Instalador firmado |

## Riesgos y decisiones abiertas

- **VSS desde .NET.** VSS es una API COM pensada para C++. Hay que validar la vía elegida al
  empezar la etapa 3.
- **WPF dentro de WinPE.** No está garantizado que una app WPF de .NET 10 funcione en WinPE;
  por eso el motor y la CLI son independientes. Validar al empezar la etapa 8.
- **Sectores 512 frente a 4K nativo.** Hasta nuevo aviso, origen y destino deben tener el
  mismo tamaño de sector lógico; si no, se rechaza el clonado.
- **Destino más pequeño (etapa 5).** No admite copia bloque a bloque: exige particiones
  nuevas, copia de archivos desde la instantánea y regenerar el arranque.
- **Cifrado.** Leído desde la instantánea, el contenido sale descifrado; el clon queda sin
  cifrar y hay que reactivar el cifrado después.
- **Hardware distinto (etapa 9).** El arranque depende de los controladores de almacenamiento
  del equipo nuevo y la licencia de Windows puede no transferirse. La app puede preparar y
  avisar, no garantizar.
- **Imagen a archivo (VHDX).** No está en el alcance; valorar si se añade como alternativa al
  clon disco a disco para copias de seguridad.
- Pendiente: nombre definitivo del producto e instalador a usar.

## Comandos

La solución es `Cloner.slnx`. Desde la raíz del proyecto:

```
dotnet build                                 # compila todo; las advertencias son errores
dotnet test                                  # todas las pruebas
dotnet test tests/Cloner.Core.Tests          # solo unitarias
dotnet run --project src/Cloner.Cli -- list  # CLI: discos y particiones (solo lectura)
dotnet run --project src/Cloner.App          # interfaz; pide elevación (UAC)
```

- Las pruebas de integración marcadas con `[AdminFact]` se omiten si la consola no es de
  administrador. Úsalo en toda prueba que monte o escriba discos virtuales.
- `Directory.Build.props` fija la versión y las opciones comunes (`Nullable`,
  `TreatWarningsAsErrors`, analizadores). Todos los proyectos apuntan a `net10.0-windows`.
- `Cloner.App` lleva un manifiesto `requireAdministrator`; `Cloner.Cli` no, y comprueba los
  privilegios en ejecución con `Elevation.IsAdministrator()`.
