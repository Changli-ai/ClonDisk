using System.Globalization;
using Cloner.Core.Inventory;
using Cloner.Core.Layout;
using Cloner.Core.PartitionTables;

namespace Cloner.Core.Cloning;

/// <summary>Datos del equipo que intervienen en las reglas de seguridad.</summary>
internal sealed record CloneEnvironment
{
    /// <summary>Disco desde el que se ejecuta la aplicación, si se pudo averiguar.</summary>
    public int? HostDiskNumber { get; init; }

    /// <summary>Se pueden crear instantáneas VSS (Windows en marcha; no en WinPE).</summary>
    public bool SnapshotsAvailable { get; init; }
}

/// <summary>
/// Decide si un clonado es posible y produce su plan. Aquí está toda la lógica que decide qué
/// disco se escribe (RS-02, RS-06), en qué orden (RS-05), de dónde se lee cada partición
/// (disco o instantánea) y qué se copia (todo o solo los bloques en uso). No toca discos.
/// </summary>
internal static class ClonePlanner
{
    /// <summary>Al empezar se borra también el final del destino, donde puede haber una GPT de respaldo antigua.</summary>
    public const int TailClearBytes = 1024 * 1024;

    /// <summary>Margen sobre el espacio usado al estimar lo que se copia de un volumen NTFS.</summary>
    private const long UsedBlocksMargin = 64L * 1024 * 1024;

    private const long MbrLimitSectors = uint.MaxValue;
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-ES");

    /// <param name="sourceTable">Tabla leída del principio del origen; nulo si no se pudo leer.</param>
    public static ClonePlanResult Create(
        DiskInfo source,
        DiskInfo target,
        PartitionTableInfo? sourceTable,
        CloneEnvironment environment,
        CloneOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();
        CheckTarget(source, target, environment, options, problems);
        CheckSource(source, sourceTable, environment, problems);
        CheckCompatibility(source, target, problems);

        if (problems.Count > 0 || sourceTable is null)
        {
            return new ClonePlanResult { Problems = problems, Warnings = BuildWarnings(source, target, [], TargetLayout.Same(source.Partitions), newIdentifiers: false) };
        }

        // RF-13, RF-16, RF-18: distribución e identificadores del destino.
        var layoutWarnings = new List<string>();
        var converting = StyleConversion.Requested(sourceTable, options.TargetStyle);
        var targetStyle = converting ? options.TargetStyle!.Value : sourceTable.Style;
        var createEfi = converting && StyleConversion.Check(source, targetStyle, problems, layoutWarnings);
        var layout = LayoutPlanner.Plan(source, target, sourceTable, options.Sizing, options.Alignment, problems, layoutWarnings, converting ? targetStyle : null, createEfi);
        if (layout.Placements.Any(p => p.Shrinks) && options.Mode == CloneMode.SectorBySector)
        {
            problems.Add("El destino es más pequeño y hay que reducir particiones, lo que no es posible copiando sector a sector. Usa la copia inteligente.");
        }

        // Una tabla de otro estilo lleva siempre identificadores nuevos.
        var identity = (options.NewIdentifiers || converting) && sourceTable.Style != PartitionStyle.Raw
            ? TableIdentity.CreateNew(targetStyle, source.Partitions, layout.Created.Count)
            : null;
        var rebuild = layout.Changes || identity is not null;

        var steps = rebuild
            ? BuildRebuiltSteps(source, target.SizeBytes, sourceTable, layout, environment, options)
            : BuildSteps(source, target.SizeBytes, sourceTable, environment, options, problems);
        var warnings = BuildWarnings(source, target, steps, layout, identity is not null);
        warnings.AddRange(layoutWarnings);
        if (problems.Count > 0)
        {
            return new ClonePlanResult { Problems = problems, Warnings = warnings };
        }

        var plan = new ClonePlan
        {
            Source = DiskIdentity.From(source),
            Target = DiskIdentity.From(target),
            Options = options,
            SourceIsSystemDisk = source.IsSystemDisk,
            SourceIsReadOnly = source.IsReadOnly,
            SourcePartitions = source.Partitions,
            TargetPartitions = target.Partitions,
            Steps = steps,
            AdjustsGpt = targetStyle == PartitionStyle.Gpt && (target.SizeBytes > source.SizeBytes || rebuild),
            Layout = rebuild ? layout : TargetLayout.Same(source.Partitions),
            NewIdentity = identity,
            Warnings = warnings,
            SourceTable = sourceTable,
        };

        return new ClonePlanResult { Plan = plan, Warnings = warnings };
    }

    /// <summary>Volúmenes de los que VSS puede hacer instantáneas.</summary>
    internal static bool SupportsSnapshots(string? fileSystem) =>
        string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileSystem, "ReFS", StringComparison.OrdinalIgnoreCase);

    // RS-02: el destino no puede ser el origen, el disco del sistema ni el de la aplicación.
    private static void CheckTarget(DiskInfo source, DiskInfo target, CloneEnvironment environment, CloneOptions options, List<string> problems)
    {
        if (source.Number == target.Number)
        {
            problems.Add("El origen y el destino son el mismo disco.");
            return;
        }

        if (target.IsSystemDisk)
        {
            problems.Add($"El disco {target.Number} es el disco del sistema en uso y no se puede usar como destino.");
        }

        if (environment.HostDiskNumber == target.Number)
        {
            problems.Add($"La aplicación se está ejecutando desde el disco {target.Number}; no se puede usar como destino.");
        }

        if (target.IsReadOnly)
        {
            problems.Add($"El disco {target.Number} está marcado como de solo lectura.");
        }

        if (target.IsOffline)
        {
            problems.Add($"El disco {target.Number} está sin conexión. Ponlo en línea en Administración de discos antes de clonar.");
        }

        if (options.VirtualTargetOnly && target.Bus != BusKind.Virtual)
        {
            problems.Add($"Solo se admiten destinos virtuales (VHD/VHDX) y el disco {target.Number} no lo es.");
        }
    }

    private static void CheckSource(DiskInfo source, PartitionTableInfo? table, CloneEnvironment environment, List<string> problems)
    {
        // RF-10: el disco del sistema solo se puede copiar en caliente a partir de instantáneas.
        if (source.IsSystemDisk)
        {
            var windows = source.Partitions.FirstOrDefault(p => p.ContainsWindows);
            if (!environment.SnapshotsAvailable)
            {
                problems.Add($"El disco {source.Number} es el disco del sistema en uso y aquí no se pueden crear instantáneas (VSS) para copiarlo en caliente.");
            }
            else if (windows?.Volume is not { } volume || !SupportsSnapshots(volume.FileSystem))
            {
                problems.Add($"No se puede hacer una instantánea del volumen de Windows del disco {source.Number}.");
            }
        }

        if (source.Partitions.Any(p => p.Kind == PartitionKind.Dynamic))
        {
            problems.Add($"El disco {source.Number} es un disco dinámico, que no se admite.");
        }

        if (table is null)
        {
            problems.Add($"No se pudo leer la tabla de particiones del disco {source.Number}.");
            return;
        }

        if (table.Defect is not null)
        {
            problems.Add($"La tabla de particiones del disco {source.Number} no es válida: {table.Defect}.");
        }
        else if (table.Style != source.PartitionStyle || !string.Equals(table.Identifier, source.Identifier, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"Lo leído del disco {source.Number} no coincide con lo que informa Windows. Actualiza la lista de discos.");
        }
    }

    // RS-06: capacidad suficiente y tamaños de sector compatibles.
    private static void CheckCompatibility(DiskInfo source, DiskInfo target, List<string> problems)
    {
        if (source.Number == target.Number)
        {
            return;
        }

        if (source.LogicalSectorSize != target.LogicalSectorSize)
        {
            problems.Add($"Los discos tienen distinto tamaño de sector lógico ({source.LogicalSectorSize} B en el origen y {target.LogicalSectorSize} B en el destino).");
        }

        if (source.SizeBytes < 2L * (PartitionTable.HeadBytes + TailClearBytes))
        {
            problems.Add($"El disco {source.Number} es demasiado pequeño para clonarlo.");
        }

        if (source.LogicalSectorSize <= 0 || source.SizeBytes % source.LogicalSectorSize != 0)
        {
            problems.Add($"La capacidad del disco {source.Number} no es un número entero de sectores.");
        }
    }

    private static List<string> BuildWarnings(DiskInfo source, DiskInfo target, IReadOnlyList<CloneStep> steps, TargetLayout layout, bool newIdentifiers)
    {
        var warnings = new List<string>();

        if (source.IsSystemDisk)
        {
            warnings.Add("Es el disco del sistema: el clon arrancará igual que el original. Para arrancar desde él, retira o desconecta el disco original.");
        }

        var snapshotNames = steps.Where(s => s.FromSnapshot && s.Partition is not null).Select(s => VolumeName(s.Partition!)).Distinct().ToList();
        if (snapshotNames.Count > 0)
        {
            warnings.Add($"Se copiará a partir de una instantánea de {string.Join(", ", snapshotNames)}: lo que cambie en esos volúmenes después de empezar no estará en el clon.");
        }

        if (DiskHealth.Assess(source) != HealthIssues.None)
        {
            warnings.Add("El origen presenta problemas de salud: la copia puede detenerse si encuentra sectores ilegibles.");
        }

        var encrypted = source.Partitions.Where(p => p.Encryption is EncryptionState.On or EncryptionState.Suspended).ToList();
        if (encrypted.Any(p => steps.Any(s => s.FromSnapshot && s.Partition == p)))
        {
            warnings.Add("Hay volúmenes cifrados con BitLocker que se copian desde una instantánea: en el clon quedarán sin cifrar. Vuelve a activar el cifrado después.");
        }
        else if (encrypted.Count > 0)
        {
            warnings.Add("Hay particiones cifradas con BitLocker: se copian cifradas y el clon se abrirá con la misma contraseña o clave de recuperación.");
        }

        if (newIdentifiers)
        {
            warnings.Add("El clon recibirá identificadores de disco y de partición nuevos: podrá estar conectado a la vez que el original. Si es un disco de Windows, su arranque se rehace al terminar.");
        }
        else if (source.Identifier is not null)
        {
            warnings.Add("El clon tendrá el mismo identificador de disco que el origen. Para que Windows no se lo cambie mientras los dos están conectados, ClonDisk lo deja sin conexión al terminar: apaga el equipo antes de cambiar los discos.");
        }

        foreach (var placement in layout.Placements.Where(p => p.Shrinks))
        {
            warnings.Add($"La partición {placement.Partition.Number} ({VolumeName(placement.Partition)}) pasa de {Size(placement.Partition.SizeBytes)} a {Size(placement.TargetSize)}: "
                + "se formatea de nuevo y sus archivos se copian uno a uno. No se copian los archivos de paginación e hibernación (Windows los vuelve a crear) ni los cifrados con EFS.");
        }

        if (layout.FitsSmallerTarget && !layout.Placements.Any(p => p.Shrinks))
        {
            warnings.Add("El destino es más pequeño que el origen, pero las particiones caben sin reducirlas.");
        }

        foreach (var placement in layout.Placements.Where(p => p.Grows))
        {
            warnings.Add($"La partición {placement.Partition.Number} ({VolumeName(placement.Partition)}) pasa de {Size(placement.Partition.SizeBytes)} a {Size(placement.TargetSize)}: su sistema de archivos se amplía al terminar la copia.");
        }

        if (layout.Aligned)
        {
            warnings.Add("Las particiones se recolocan alineadas a 1 MiB, lo recomendado en un SSD.");
        }

        var unused = target.SizeBytes - source.SizeBytes - layout.Placements.Sum(p => p.Growth);
        if (unused > 0 && !layout.Placements.Any(p => p.Grows))
        {
            var extra = unused;
            warnings.Add(source.PartitionStyle == PartitionStyle.Gpt
                ? $"El destino es mayor: la tabla GPT se ajusta a su tamaño y los {Size(extra)} sobrantes quedan sin asignar."
                : $"El destino es mayor: los {Size(extra)} sobrantes quedan sin asignar.");
        }

        if ((layout.Style ?? source.PartitionStyle) == PartitionStyle.Mbr && target.LogicalSectorSize > 0
            && target.SizeBytes / target.LogicalSectorSize > MbrLimitSectors)
        {
            warnings.Add($"Con tabla MBR solo se pueden usar los primeros {Size(MbrLimitSectors * target.LogicalSectorSize)} del destino.");
        }

        return warnings;
    }

    /// <summary>
    /// Orden de escritura (RS-05): primero se borran la tabla y el final del destino, después se
    /// copian los datos y la tabla de particiones va al final. Si la copia se interrumpe, el
    /// destino queda sin tabla y Windows no lo toma por un disco válido.
    /// </summary>
    private static List<CloneStep> BuildSteps(
        DiskInfo source,
        long targetBytes,
        PartitionTableInfo table,
        CloneEnvironment environment,
        CloneOptions options,
        List<string> problems)
    {
        var sourceBytes = source.SizeBytes;
        long head = PartitionTable.HeadBytes;
        var steps = new List<CloneStep>
        {
            new(CloneStepKind.Clear, 0, head, "Borrar la tabla de particiones del destino"),
        };

        var tailClearStart = Math.Max(head, targetBytes - TailClearBytes);
        steps.Add(new CloneStep(CloneStepKind.Clear, tailClearStart, targetBytes - tailClearStart, "Borrar el final del destino"));

        // En GPT, la copia de respaldo (entradas + cabecera) está al final del origen.
        (long Start, long End)? backup = null;
        var adjustGpt = false;
        if (table.Gpt is { } gpt)
        {
            var sectorSize = gpt.SectorSize;
            var start = (gpt.AlternateLba - gpt.EntriesSectors) * sectorSize;
            var end = (gpt.AlternateLba + 1) * sectorSize;
            if (gpt.AlternateLba <= 1 || end > sourceBytes || start < head || gpt.LastUsableLba * sectorSize >= start)
            {
                problems.Add("La tabla GPT del origen no es coherente con el tamaño del disco.");
                return steps;
            }

            backup = (start, end);
            adjustGpt = targetBytes > sourceBytes;
            if (adjustGpt)
            {
                // La copia de respaldo antigua quedaría en mitad del destino: se borra.
                steps.Add(new CloneStep(CloneStepKind.Clear, start, end - start, "Borrar la posición antigua de la GPT de respaldo"));
            }
        }

        if (backup is { } range)
        {
            AddBody(steps, source, head, range.Start, environment, options);
            AddBody(steps, source, range.End, sourceBytes, environment, options);
            if (adjustGpt)
            {
                var length = range.End - range.Start;
                steps.Add(new CloneStep(CloneStepKind.WriteBackupGpt, targetBytes - length, length, "Escribir la GPT de respaldo al final del destino"));
            }
            else
            {
                steps.Add(new CloneStep(CloneStepKind.Copy, range.Start, range.End - range.Start, "Copiar la GPT de respaldo"));
            }
        }
        else
        {
            AddBody(steps, source, head, sourceBytes, environment, options);
        }

        steps.Add(new CloneStep(
            CloneStepKind.WritePartitionTable,
            0,
            head,
            adjustGpt ? "Escribir la tabla de particiones, ajustada al tamaño del destino" : "Escribir la tabla de particiones"));
        return steps;
    }

    /// <summary>
    /// Pasos cuando la tabla se genera de nuevo (particiones movidas o ampliadas, o identificadores
    /// nuevos): cada partición se copia entera a su posición en el destino, el espacio sin
    /// particionar no se copia y la tabla nueva se escribe al final (RS-05).
    /// </summary>
    private static List<CloneStep> BuildRebuiltSteps(
        DiskInfo source,
        long targetBytes,
        PartitionTableInfo table,
        TargetLayout layout,
        CloneEnvironment environment,
        CloneOptions options)
    {
        long head = PartitionTable.HeadBytes;
        var tailClearStart = Math.Max(head, targetBytes - TailClearBytes);
        var steps = new List<CloneStep>
        {
            new(CloneStepKind.Clear, 0, head, "Borrar la tabla de particiones del destino"),
            new(CloneStepKind.Clear, tailClearStart, targetBytes - tailClearStart, "Borrar el final del destino"),
        };

        foreach (var created in layout.Created)
        {
            var name = created.Kind == PartitionKind.EfiSystem ? "la partición EFI nueva" : "la partición reservada nueva";
            steps.Add(new CloneStep(CloneStepKind.Clear, created.Offset, Math.Min(created.Size, LayoutPlanner.MiB), $"Borrar el principio de {name}"));
        }

        foreach (var placement in layout.Placements)
        {
            var partition = placement.Partition;
            if (placement.Shrinks)
            {
                steps.AddRange(FileSteps(source, placement, environment));
                continue;
            }

            var step = PartitionStep(source, partition, partition.OffsetBytes, partition.OffsetBytes + partition.SizeBytes, environment, options);
            var change = placement.Moves ? (placement.Grows ? " a su nueva posición, ampliada" : " a su nueva posición") : string.Empty;

            // Lo que caiga dentro del primer MiB del destino viaja con la tabla, que se escribe la última.
            var skip = Math.Max(0, head - placement.TargetOffset);
            if (skip >= partition.SizeBytes)
            {
                continue;
            }

            steps.Add(step with
            {
                Offset = placement.TargetOffset + skip,
                SourceOffset = partition.OffsetBytes + skip,
                Length = partition.SizeBytes - skip,
                EstimatedBytes = Math.Min(step.ExpectedBytes, partition.SizeBytes - skip),
                Description = step.Description + change,
            });
        }

        if ((layout.Style ?? table.Style) == PartitionStyle.Gpt)
        {
            var sectorSize = source.LogicalSectorSize;
            var entriesSectors = layout.Style is null ? table.Gpt!.EntriesSectors : StyleConversion.GptEntriesSectors(sectorSize);
            var length = (entriesSectors + 1L) * sectorSize;
            steps.Add(new CloneStep(CloneStepKind.WriteBackupGpt, targetBytes - length, length, "Escribir la GPT de respaldo al final del destino"));
        }

        steps.Add(new CloneStep(
            CloneStepKind.WritePartitionTable,
            0,
            head,
            layout.Style is { } style ? $"Escribir la tabla de particiones nueva, en {StyleConversion.Name(style)}" : "Escribir la tabla de particiones nueva"));
        return steps;
    }

    /// <summary>
    /// Partición reducida: se borra su principio, para que no quede rastro de lo que hubiera en el
    /// destino, y después del resto de la copia se formatea y se llena archivo a archivo.
    /// </summary>
    private static IEnumerable<CloneStep> FileSteps(DiskInfo source, PartitionPlacement placement, CloneEnvironment environment)
    {
        var partition = placement.Partition;
        var volume = partition.Volume!;
        var fromSnapshot = environment.SnapshotsAvailable && !source.IsReadOnly && SupportsSnapshots(volume.FileSystem);
        var clear = Math.Min(placement.TargetSize, LayoutPlanner.MiB);
        yield return new CloneStep(CloneStepKind.Clear, placement.TargetOffset, clear, $"Borrar el principio de la partición {partition.Number} en el destino");
        yield return new CloneStep(
            CloneStepKind.CopyFiles,
            placement.TargetOffset,
            placement.TargetSize,
            $"Copiar los archivos de la partición {partition.Number} ({VolumeName(partition)})"
                + (fromSnapshot ? " desde una instantánea" : string.Empty)
                + $" a una partición de {Size(placement.TargetSize)}")
        {
            Partition = partition,
            FromSnapshot = fromSnapshot,
            EstimatedBytes = volume.UsedBytes,
            SourceOffset = partition.OffsetBytes,
        };
    }

    /// <summary>Pasos para el tramo [start, end) del origen: una copia por partición y los huecos entre ellas.</summary>
    private static void AddBody(List<CloneStep> steps, DiskInfo source, long start, long end, CloneEnvironment environment, CloneOptions options)
    {
        if (end <= start)
        {
            return;
        }

        var extended = source.Partitions.FirstOrDefault(p => p.Kind == PartitionKind.Extended);
        var cursor = start;
        foreach (var partition in source.Partitions
            .Where(p => p.Kind != PartitionKind.Extended && p.OffsetBytes + p.SizeBytes > start && p.OffsetBytes < end)
            .OrderBy(p => p.OffsetBytes))
        {
            var partitionStart = Math.Max(partition.OffsetBytes, cursor);
            var partitionEnd = Math.Min(partition.OffsetBytes + partition.SizeBytes, end);
            AddGap(steps, cursor, partitionStart, extended, options);
            if (partitionEnd > partitionStart)
            {
                steps.Add(PartitionStep(source, partition, partitionStart, partitionEnd, environment, options));
            }

            cursor = Math.Max(cursor, partitionEnd);
        }

        AddGap(steps, cursor, end, extended, options);
    }

    private static CloneStep PartitionStep(DiskInfo source, PartitionInfo partition, long start, long end, CloneEnvironment environment, CloneOptions options)
    {
        var volume = partition.Volume;
        var fromSnapshot = environment.SnapshotsAvailable && !source.IsReadOnly && volume is not null && SupportsSnapshots(volume.FileSystem);
        var usedBlocksOnly = options.Mode == CloneMode.UsedBlocks
            && string.Equals(volume?.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)
            && (fromSnapshot || source.IsReadOnly);

        var length = end - start;
        var estimate = usedBlocksOnly ? Math.Min(length, volume!.UsedBytes + UsedBlocksMargin) : length;

        var description = $"Copiar la partición {partition.Number} ({VolumeName(partition)})"
            + (fromSnapshot ? " desde una instantánea" : string.Empty)
            + (usedBlocksOnly ? ", solo los bloques en uso" : string.Empty);

        return new CloneStep(CloneStepKind.CopyPartition, start, length, description)
        {
            Partition = partition,
            FromSnapshot = fromSnapshot,
            UsedBlocksOnly = usedBlocksOnly,
            EstimatedBytes = estimate,
        };
    }

    /// <summary>
    /// Espacio sin particionar: sector a sector se copia; en la copia inteligente se omite,
    /// salvo dentro de una partición extendida, donde están las tablas de las lógicas (EBR).
    /// </summary>
    private static void AddGap(List<CloneStep> steps, long start, long end, PartitionInfo? extended, CloneOptions options)
    {
        if (end <= start)
        {
            return;
        }

        if (options.Mode == CloneMode.SectorBySector)
        {
            steps.Add(new CloneStep(CloneStepKind.Copy, start, end - start, "Copiar el espacio sin particionar"));
            return;
        }

        if (extended is not null)
        {
            var from = Math.Max(start, extended.OffsetBytes);
            var to = Math.Min(end, extended.OffsetBytes + extended.SizeBytes);
            if (to > from)
            {
                steps.Add(new CloneStep(CloneStepKind.Copy, from, to - from, "Copiar las tablas de las particiones lógicas"));
            }
        }
    }

    private static string VolumeName(PartitionInfo partition) => partition.DriveLetter is { } letter
        ? $"{letter}:"
        : partition.Volume?.Label ?? InventoryText.Kind(partition.Kind);

    private static string Size(long bytes) => ByteSize.Format(bytes, Culture);

    private static string Bytes(long bytes) => bytes.ToString("N0", Culture);
}
