using System.Globalization;
using Cloner.Core.Inventory;
using Cloner.Core.PartitionTables;

namespace Cloner.Core.Cloning;

/// <summary>Datos del equipo que intervienen en las reglas de seguridad.</summary>
internal sealed record CloneEnvironment
{
    /// <summary>Disco desde el que se ejecuta la aplicación, si se pudo averiguar.</summary>
    public int? HostDiskNumber { get; init; }
}

/// <summary>
/// Decide si un clonado sector a sector es posible y produce su plan. Aquí está toda la lógica
/// que decide qué disco se escribe (RS-02, RS-06) y en qué orden (RS-05). No toca discos.
/// </summary>
internal static class ClonePlanner
{
    /// <summary>Al empezar se borra también el final del destino, donde puede haber una GPT de respaldo antigua.</summary>
    public const int TailClearBytes = 1024 * 1024;

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
        CheckSource(source, sourceTable, problems);
        CheckCompatibility(source, target, problems);

        var warnings = BuildWarnings(source, target);
        if (problems.Count > 0 || sourceTable is null)
        {
            return new ClonePlanResult { Problems = problems, Warnings = warnings };
        }

        var steps = BuildSteps(source.SizeBytes, target.SizeBytes, sourceTable, problems);
        if (problems.Count > 0)
        {
            return new ClonePlanResult { Problems = problems, Warnings = warnings };
        }

        var plan = new ClonePlan
        {
            Source = DiskIdentity.From(source),
            Target = DiskIdentity.From(target),
            Options = options,
            SourcePartitions = source.Partitions,
            TargetPartitions = target.Partitions,
            Steps = steps,
            AdjustsGpt = sourceTable.Gpt is not null && target.SizeBytes > source.SizeBytes,
            Warnings = warnings,
            SourceTable = sourceTable,
        };

        return new ClonePlanResult { Plan = plan, Warnings = warnings };
    }

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

    private static void CheckSource(DiskInfo source, PartitionTableInfo? table, List<string> problems)
    {
        if (source.IsSystemDisk)
        {
            problems.Add($"El disco {source.Number} es el disco del sistema en uso. Clonarlo con Windows en marcha todavía no está disponible.");
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

        if (target.SizeBytes < source.SizeBytes)
        {
            problems.Add($"El destino ({Size(target.SizeBytes)}) es más pequeño que el origen ({Size(source.SizeBytes)}).");
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

    private static List<string> BuildWarnings(DiskInfo source, DiskInfo target)
    {
        var warnings = new List<string>();

        if (DiskHealth.Assess(source) != HealthIssues.None)
        {
            warnings.Add("El origen presenta problemas de salud: la copia puede detenerse si encuentra sectores ilegibles.");
        }

        if (source.Partitions.Any(p => p.Encryption is EncryptionState.On or EncryptionState.Suspended))
        {
            warnings.Add("Hay particiones cifradas con BitLocker: se copian cifradas y el clon se abrirá con la misma contraseña o clave de recuperación.");
        }

        if (source.Identifier is not null)
        {
            warnings.Add("El clon tendrá el mismo identificador de disco que el origen: si ambos quedan conectados a la vez, Windows dejará uno de los dos sin conexión.");
        }

        var extra = target.SizeBytes - source.SizeBytes;
        if (extra > 0)
        {
            warnings.Add(source.PartitionStyle == PartitionStyle.Gpt
                ? $"El destino es mayor: la tabla GPT se ajusta a su tamaño y los {Size(extra)} sobrantes quedan sin asignar."
                : $"El destino es mayor: los {Size(extra)} sobrantes quedan sin asignar.");
        }

        if (source.PartitionStyle == PartitionStyle.Mbr && target.LogicalSectorSize > 0
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
    private static List<CloneStep> BuildSteps(long sourceBytes, long targetBytes, PartitionTableInfo table, List<string> problems)
    {
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
            AddCopy(steps, head, range.Start);
            AddCopy(steps, range.End, sourceBytes);
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
            AddCopy(steps, head, sourceBytes);
        }

        steps.Add(new CloneStep(
            CloneStepKind.WritePartitionTable,
            0,
            head,
            adjustGpt ? "Escribir la tabla de particiones, ajustada al tamaño del destino" : "Escribir la tabla de particiones"));
        return steps;
    }

    private static void AddCopy(List<CloneStep> steps, long start, long end)
    {
        if (end > start)
        {
            steps.Add(new CloneStep(CloneStepKind.Copy, start, end - start, "Copiar los datos"));
        }
    }

    private static string Size(long bytes) => ByteSize.Format(bytes, Culture);
}
