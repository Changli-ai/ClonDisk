# ClonDisk

**ClonDisk** es una aplicación de escritorio para **Windows 10 y 11** que clona discos y
particiones entre HDD y SSD (HDD↔SSD, HDD↔HDD, SSD↔SSD; SATA, M.2 y NVMe, internos o por USB),
**incluido el disco del sistema con Windows en marcha**. La idea es sencilla: retiras el disco
original, pones el clon y el equipo arranca y funciona igual que antes, con tus archivos,
programas y ajustes.

> **Gratis, sin anuncios y de código abierto.** ClonDisk no cuesta nada, no muestra publicidad,
> no pide registrarse, no tiene funciones «premium» bloqueadas, no se conecta a internet y no
> envía telemetría. Su código está publicado aquí bajo la licencia **GPL-3.0**: puedes leerlo,
> compilarlo, modificarlo y compartirlo.

> **Estado: en desarrollo (versión 0.1.0).** El clonado ya funciona y está probado con discos
> virtuales y en un equipo real, pero aún no se ha confirmado que un clon del disco del sistema
> arranque, y varias funciones están por hacer (ver [Hoja de ruta](#hoja-de-ruta)). Úsalo con
> copia de seguridad de lo importante.

---

## ¿Para qué sirve?

| Caso | Qué hace ClonDisk |
|---|---|
| **Ampliar el disco** | Pasa todo a un disco más grande y amplía la partición principal o reparte el espacio libre. |
| **Migrar Windows a un SSD** | Copia la instalación de Windows de un HDD a un SSD (o de un SSD a otro) sin reinstalar. |
| **Pasar a un disco más pequeño** | Si los datos caben, reduce las particiones y copia el contenido para que entre en un SSD menor. |
| **Cambiar entre BIOS y UEFI** | Convierte la tabla del clon entre MBR y GPT y rehace el arranque de Windows. |
| **Tener un repuesto inmediato** | Mantén un clon completo que puede sustituir al original si falla, sin restaurar nada. |

## Características

### Clonado
- **Disco completo, incluido el del sistema en uso.** Copia Windows mientras lo estás usando
  gracias a las **instantáneas VSS** de Windows: lo que se copia es una foto coherente del momento
  en que empezó el clonado.
- **Copia inteligente (por defecto):** de los volúmenes NTFS solo copia los bloques en uso, así que
  clonar un disco de 1 TB con 350 GB ocupados copia unos 350 GB, no 1 TB.
- **Copia sector a sector:** copia íntegra de todo el disco, también para sistemas de archivos que
  no reconoce.
- **Destino más grande:** mantener los tamaños, **ampliar la partición principal** (la de Windows o
  la de datos mayor) o **repartir el espacio** entre las particiones de datos.
- **Destino más pequeño:** si los datos caben, reduce las particiones NTFS de datos (sin bajar de lo
  ocupado más un margen) y copia su contenido archivo a archivo, conservando permisos, flujos
  alternativos, enlaces duros, uniones, nombres cortos, atributos y fechas. Si no caben, te dice
  qué tamaño mínimo necesitarías.
- **Conversión MBR ↔ GPT:** para pasar un Windows de arranque BIOS a UEFI (crea la partición EFI)
  o al revés, rehaciendo el arranque con `bcdboot`.
- **Alineación a 1 MiB** automática cuando el destino es un SSD.
- **Identificadores del disco:** el clon puede conservar los del original (para sustituirlo) o
  recibir unos nuevos para tener los dos discos conectados a la vez; en ese caso se ajusta el
  arranque de Windows.

### Seguridad de tus datos
- **El disco de origen nunca se escribe:** se abre siempre en solo lectura.
- **No deja elegir un destino peligroso:** ni el disco del sistema en uso, ni el disco desde el
  que se ejecuta la app, ni el propio origen.
- **Confirmación explícita:** antes de escribir muestra el origen, el destino y **todo lo que se
  va a borrar**, y te pide confirmarlo.
- **Identifica los discos por número de serie y GUID**, no solo por su número, y lo comprueba de
  nuevo justo antes de escribir por si algo cambió.
- **Un clon interrumpido no parece válido:** la tabla de particiones se escribe al final; si
  cancelas o falla, el destino queda sin tabla.
- **Verificación opcional:** al terminar relee el destino y lo compara bloque a bloque con lo
  copiado (huellas XxHash).

### Comodidad
- Interfaz clara en **español**, con barra lateral y tarjetas de origen y destino.
- **Progreso en tiempo real:** porcentaje, velocidad, tiempo restante y lo que se está copiando.
- **Cancelación segura** en cualquier momento, con aviso visible mientras se detiene.
- **Registro detallado** de cada operación, exportable.
- **Avisos útiles** antes de empezar: BitLocker, salud del disco (SMART), espacio, identificadores…
- **Línea de comandos** (`clondisk-cli`) para automatizar o usar sin interfaz.
- **Rápido:** lectura y escritura en hilos separados con búferes grandes; la copia archivo a
  archivo usa varios hilos a la vez.
- **Inspección de discos** en solo lectura: modelo, serie, capacidad, tipo, bus, tabla de
  particiones, sistemas de archivos, espacio usado y salud.

## Requisitos

- Windows 10 (22H2) o Windows 11, de 64 bits.
- Permisos de administrador (la aplicación los pide al abrirse).
- Para clonar el disco del sistema en caliente, el volumen de Windows debe ser NTFS.
- Para compilarlo: [SDK de .NET 10](https://dotnet.microsoft.com/download).

La versión publicada es **autocontenida**: no hace falta instalar .NET para usarla.

## Descarga

Cuando haya versiones publicadas estarán en la sección **Releases** de este repositorio. Mientras
tanto puedes compilarlo tú mismo (ver abajo). El ejecutable aún no está firmado, así que Windows
SmartScreen puede avisar al abrirlo: «Más información» → «Ejecutar de todas formas».

## Cómo usarlo

### Interfaz (`ClonDisk.exe`)
1. Abre ClonDisk y acepta el aviso de permisos de Windows.
2. Elige **Disco** (cualquier disco) o **Sistema** (el disco de Windows en uso, ya preseleccionado).
3. Pulsa **+** en la tarjeta de origen y en la de destino para elegir los discos y luego
   **Siguiente**.
4. Revisa las opciones (modo de copia, uso del espacio, identificadores, tabla de particiones) y el
   plan: qué se copiará, cómo quedarán las particiones y **qué se borrará**.
5. Marca la confirmación y pulsa **Borrar el destino y clonar**.

Para arrancar desde el clon de un disco de Windows, apaga el equipo y retira o desconecta el disco
original. Si los dos quedan conectados con los mismos identificadores, Windows deja uno sin
conexión.

### Línea de comandos (`clondisk-cli.exe`)
Desde una consola de administrador:

```
clondisk-cli list                    # discos y particiones (solo lectura)
clondisk-cli plan 0 1                # qué haría un clonado del disco 0 al 1, sin escribir nada
clondisk-cli clone 0 1               # clona; pide escribir BORRAR para confirmar
```

Opciones de `plan` y `clone`:

| Opción | Efecto |
|---|---|
| `--sector` | Copia sector a sector (por defecto, solo los bloques en uso). |
| `--extend` / `--proportional` | Si el destino es mayor, amplía la partición principal o reparte el espacio. |
| `--align` / `--no-align` | Alinea las particiones a 1 MiB siempre o nunca (por defecto, solo en SSD). |
| `--new-ids` | Identificadores nuevos, para usar el clon junto al original. |
| `--gpt` / `--mbr` | Convierte la tabla del clon a GPT (UEFI) o MBR (BIOS). |
| `--no-verify` | No relee el destino al terminar. |
| `--virtual-only` | Solo admite discos virtuales (VHD/VHDX), para pruebas. |

Códigos de salida de `clone`: 0 correcto, 1 argumentos, 3 bloqueado por las reglas de seguridad,
4 cancelado, 5 fallo, 6 verificación fallida.

## Compilar desde el código

```
git clone https://github.com/Changli-ai/ClonDisk.git
cd ClonDisk
dotnet build                                   # compila todo
dotnet test tests/Cloner.Core.Tests            # pruebas unitarias
dotnet run --project src/Cloner.App            # abre la interfaz (pide permisos)
```

Versión autocontenida, lista para llevar a otro equipo:

```
dotnet publish src/Cloner.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
dotnet publish src/Cloner.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Las pruebas de integración (`tests/Cloner.IntegrationTests`) crean y clonan discos virtuales VHD,
así que necesitan una consola de administrador; sin ella se omiten.

## Cómo está hecho

- **C# sobre .NET 10**, interfaz **WPF** (MVVM con CommunityToolkit.Mvvm) y llamadas a Windows con
  CsWin32.
- Acceso directo a los discos (`\\.\PhysicalDriveN`) sin caché, con `DeviceIoControl` para leer y
  escribir tablas de particiones.
- Instantáneas **VSS** desde C# mediante la interfaz COM de Windows, sin bibliotecas externas.
- Inventario de discos con la Storage Management API de Windows (WMI).
- Cada clonado se separa en **planificar** (un plan revisable de todo lo que se leerá y escribirá)
  y **ejecutar**, de modo que la interfaz te enseña el plan antes y las pruebas lo validan sin
  tocar discos.

```
src/
  Cloner.Core/              Motor: discos, tablas de particiones, VSS, planificación, copia y verificación.
  Cloner.Cli/               Línea de comandos.
  Cloner.App/               Interfaz WPF.
tests/
  Cloner.Core.Tests/        Pruebas unitarias (sin tocar discos).
  Cloner.IntegrationTests/  Pruebas con discos virtuales VHD (requieren administrador).
```

## Hoja de ruta

| Etapa | Contenido | Estado |
|---|---|---|
| 1 | Inspección de discos y particiones | Hecho |
| 2 | Clonado de discos de datos, verificación y registro | Hecho |
| 3 | Disco del sistema en caliente (VSS) y copia inteligente | Hecho; falta confirmar que el clon arranca |
| 4 | Ampliar particiones, alinear e identificadores nuevos | Hecho; falta confirmar el arranque |
| 5 | Destino más pequeño y conversión MBR ↔ GPT | Hecho; falta confirmar el arranque |
| 6 | Clonar una sola partición | Pendiente |
| 7 | Modo rescate para discos con sectores dañados | Pendiente |
| 8 | USB de arranque de emergencia (WinPE) | Pendiente |
| 9 | Preparar el clon para otro equipo con hardware distinto | Pendiente |
| 10 | Instalador firmado | Pendiente |
| 11 | Migración a un Windows 11 nuevo con tus datos y programas | Pendiente |
| 12 | Clonación por red | Pendiente |

También está pendiente poder **reanudar** un clonado cancelado.

## Limitaciones conocidas

- **Clonar borra todo el disco de destino.** Revisa bien cuál eliges.
- Aún **no se ha comprobado que un clon del disco del sistema arranque**; está en pruebas.
- Leído desde una instantánea, un volumen con **BitLocker** queda **sin cifrar** en el clon: vuelve
  a activar el cifrado después.
- Al reducir una partición, los archivos cifrados con **EFS** no se copian, y los de paginación e
  hibernación tampoco (Windows los crea de nuevo). Esa copia archivo a archivo es mucho más lenta
  que la copia por bloques, sobre todo hacia un HDD por USB.
- Windows 10/11 Home y Pro **no arrancan de forma fiable desde un disco USB**: para arrancar desde
  el clon, instálalo dentro del equipo.
- Llevar el clon a **otro equipo** puede requerir controladores y reactivar la licencia de Windows.
- Origen y destino deben tener el **mismo tamaño de sector lógico** (512 B o 4 KB).
- No admite discos dinámicos ni, al reducir o convertir, particiones lógicas MBR.

## Licencia

ClonDisk es **software libre y de código abierto** bajo la
[Licencia Pública General de GNU, versión 3](LICENSE) (GPL-3.0): puedes usarlo, estudiarlo,
modificarlo y compartirlo gratis; si distribuyes una versión modificada, debe publicarse también
bajo la GPL-3.0 con su código.

Se ofrece **tal cual, sin garantía**. Clonar discos borra datos: haz copia de lo importante antes
de usarlo.

© 2026 Changli-ai
