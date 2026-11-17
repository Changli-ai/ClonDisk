using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Hashing;
using System.Runtime.ExceptionServices;
using Cloner.Core.Devices;
using Cloner.Core.FileCopy;
using Cloner.Core.Inventory;
using Cloner.Core.Layout;
using Cloner.Core.Logging;
using Cloner.Core.PartitionTables;
using Microsoft.Extensions.Logging;

namespace Cloner.Core.Cloning;

/// <summary>
/// Aplica un plan de clonado. Lee el origen en un hilo y escribe el destino en otro, con
/// búferes alineados de 4 MiB. Calcula la huella de cada bloque escrito para la verificación.
/// </summary>
internal sealed class CloneExecutor
{
    internal const int ChunkBytes = 4 * 1024 * 1024;
    private const int BufferCount = 4;

    /// <summary>Etiqueta de una partición reducida mientras se copian sus archivos.</summary>
    internal const string IncompleteLabel = "ClonDisk incompleto";
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-ES");

    private readonly IDiskAccess _access;
    private readonly ILogger _log;
    private readonly TimeProvider _time;

    public CloneExecutor(IDiskAccess access, ILogger log, TimeProvider? time = null)
    {
        _access = access;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public CloneResult Execute(ClonePlan plan, IProgress<CloneProgress>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var started = _time.GetTimestamp();
        var state = new RunState();
        LogPlan(plan);

        IBlockDevice? source = null;
        IBlockDevice? target = null;
        ProgressMeter? meter = null;
        var volumes = new VolumeSources();
        try
        {
            Report(progress, ClonePhase.Preparing, "Comprobando los discos");
            source = _access.Open(plan.Source, writable: false);
            target = _access.Open(plan.Target, writable: true);
            CheckDevices(plan, source, target);
            var head = ReadSourceHead(source, plan);

            // Si hay que montar el clon para ajustarlo y debe conservar los identificadores del
            // origen, se copia con identificadores provisionales: con los del origen, Windows lo
            // dejaría sin conexión. Los definitivos se ponen al final.
            var targetSectors = plan.Target.SizeBytes / plan.Target.SectorSize;
            // Con particiones nuevas (la EFI de una conversión) también se copia con una tabla
            // provisional, en la que la EFI figura como de datos para poder formatearla.
            // Las particiones reducidas también: hasta formatearlas están vacías y, con letra de
            // unidad, el Explorador ofrecería formatearlas.
            var temporary = plan.NeedsFinishing && (plan.NewIdentity is null || plan.Layout.Created.Count > 0 || plan.FileSteps.Count > 0);
            var copyIdentity = plan.NewIdentity ?? (temporary ? TableIdentity.CreateNew(plan.SourceTable.Style, plan.SourcePartitions) : null);
            BuiltTable? built = null;
            CheckTargetTable(target, plan);
            cancellation.ThrowIfCancellationRequested();

            using (_access.LockVolumes(Volumes(plan.TargetPartitions), dismount: true))
            using (LockSourceVolumes(plan))
            {
                CloneLog.VolumesLocked(_log);
                if (plan.RebuildsTable)
                {
                    built = TableBuilder.Build(head, plan.SourceTable, plan.Layout, targetSectors, plan.Source.SectorSize, copyIdentity, provisional: temporary);
                }

                OpenVolumeSources(plan, volumes, progress);
                cancellation.ThrowIfCancellationRequested();

                var ranges = Expand(plan, source, volumes);
                var dataBytes = ranges.Where(r => r.Step.WritesData).Sum(r => r.Length);
                var fileBytes = plan.FileSteps.Sum(s => s.ExpectedBytes);
                meter = new ProgressMeter(ranges.Sum(r => r.Length) + (plan.Options.Verify ? dataBytes : 0) + fileBytes, _time);

                state.Phase = ClonePhase.Copying;
                var written = Copy(plan, ranges, source, target, head, built, volumes, state, meter, progress, cancellation);
                target.Flush();

                // Con particiones reducidas, el clon no está completo hasta copiar sus archivos.
                state.TargetComplete = plan.FileSteps.Count == 0;
                CloneLog.CopyFinished(_log, state.BytesWritten, state.DataHash);

                // Las instantáneas ya no hacen falta, salvo para copiar archivos: se liberan antes de verificar.
                if (plan.FileSteps.Count == 0)
                {
                    volumes.Release(completed: true);
                }
                else
                {
                    volumes.ReleaseDevices();
                }

                if (plan.Options.Verify)
                {
                    state.Phase = ClonePhase.Verifying;
                    var mismatch = Verify(target, written, meter, progress, cancellation);
                    if (mismatch is { } offset)
                    {
                        CloneLog.VerificationMismatch(_log, offset);
                        Invalidate(plan, target);
                        state.TargetComplete = false;
                        return Finish(state, started, CloneOutcome.VerificationFailed,
                            $"El destino no coincide con lo copiado a partir del byte {offset.ToString("N0", Culture)}. "
                            + "Se ha marcado como incompleto.");
                    }

                    state.Verified = true;
                    CloneLog.VerificationPassed(_log);
                }
            }

            state.Phase = ClonePhase.Finishing;
            if (plan.NeedsFinishing)
            {
                FinishClone(plan, target, head, temporary, volumes, state, meter, progress, cancellation);
            }

            progress?.Report(meter.Snapshot(ClonePhase.Finishing, "Terminando"));
            var summary = plan.FileSteps.Count > 0
                ? state.Verified
                    ? "La copia ha terminado: lo copiado por bloques se ha verificado y las particiones reducidas se han llenado archivo a archivo."
                    : "La copia ha terminado (por bloques y archivo a archivo), pero no se ha verificado."
                : state.Verified
                    ? "El destino es idéntico a lo leído del origen."
                    : "La copia ha terminado, pero no se ha verificado.";
            if (state.FileErrors > 0)
            {
                summary += $" {state.FileErrors.ToString("N0", Culture)} archivos o carpetas no se pudieron copiar; están en el registro.";
            }

            return Finish(state, started, CloneOutcome.Succeeded, summary);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            var message = state.TargetComplete
                ? "La copia había terminado, pero la verificación se canceló."
                : state.TargetModified
                    ? "El destino ha quedado incompleto y sin tabla de particiones."
                    : "No se llegó a escribir en el destino.";
            return Finish(state, started, CloneOutcome.Cancelled, message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CloneLog.CloneError(_log, ex);
            var reason = ex is IOException or UnauthorizedAccessException or CloneException ? ex.Message : $"Error inesperado: {ex.Message}";
            var message = reason.TrimEnd('.') + "." + (state.TargetComplete
                ? " La copia de los datos terminó y se verificó, pero el clon no se pudo ajustar."
                : state.TargetModified
                    ? " El destino ha quedado incompleto y sin tabla de particiones."
                    : " No se ha escrito nada en el destino.");
            return Finish(state, started, CloneOutcome.Failed, message);
        }
        finally
        {
            volumes.Release(completed: false);
            if (target is not null && state.TargetModified)
            {
                RefreshLayout(target);
            }

            target?.Dispose();
            source?.Dispose();
        }
    }

    private static void Report(IProgress<CloneProgress>? progress, ClonePhase phase, string item) =>
        progress?.Report(new CloneProgress { Phase = phase, CurrentItem = item });

    /// <summary>
    /// Bloquea los volúmenes del origen que se leen del disco (los de las instantáneas no hacen
    /// falta). La partición EFI puede estar abierta por Windows; si no se puede bloquear se copia
    /// igualmente, porque no se modifica durante el uso normal.
    /// </summary>
    private CompositeLock LockSourceVolumes(ClonePlan plan)
    {
        var locks = new CompositeLock();
        if (plan.SourceIsReadOnly)
        {
            return locks;
        }

        try
        {
            var snapshotted = plan.SnapshotPartitions.ToHashSet();
            var toLock = plan.SourcePartitions.Where(p => p.Volume is not null && !snapshotted.Contains(p)).ToList();
            var required = toLock.Where(p => p.Kind != PartitionKind.EfiSystem).ToList();
            if (required.Count > 0)
            {
                locks.Add(_access.LockVolumes(Volumes(required), dismount: false));
            }

            foreach (var partition in toLock.Where(p => p.Kind == PartitionKind.EfiSystem))
            {
                try
                {
                    locks.Add(_access.LockVolumes(Volumes([partition]), dismount: false));
                }
                catch (CloneException ex)
                {
                    CloneLog.LockSkipped(_log, DescribePartition(partition), ex.Message);
                }
            }

            return locks;
        }
        catch
        {
            locks.Dispose();
            throw;
        }
    }

    /// <summary>Crea las instantáneas y abre los volúmenes que se leen desde ellas o con su mapa de bits.</summary>
    private void OpenVolumeSources(ClonePlan plan, VolumeSources volumes, IProgress<CloneProgress>? progress)
    {
        var snapshotPartitions = plan.SnapshotPartitions;
        if (snapshotPartitions.Count > 0)
        {
            var names = string.Join(", ", snapshotPartitions.Select(DescribePartition));
            Report(progress, ClonePhase.Preparing, $"Creando instantáneas: {names}");
            volumes.Snapshots = _access.CreateSnapshots(snapshotPartitions.Select(p => p.Volume!.Path).ToList(), plan.Source.SectorSize);
            CloneLog.SnapshotsCreated(_log, names);
        }

        foreach (var step in plan.Steps.Where(s => s.Kind != CloneStepKind.CopyFiles && s.Partition is not null && (s.FromSnapshot || s.UsedBlocksOnly)))
        {
            var partition = step.Partition!;
            if (!volumes.ByPartition.ContainsKey(partition))
            {
                volumes.ByPartition[partition] = step.FromSnapshot
                    ? volumes.Snapshots!.Open(partition.Volume!.Path)
                    : _access.OpenVolume(partition.Volume!.Path, plan.Source.SectorSize);
            }
        }
    }

    /// <summary>
    /// Convierte los pasos del plan en tramos concretos: de qué dispositivo se lee cada uno y en
    /// qué posición del destino se escribe. Con copia inteligente, solo los bloques en uso.
    /// </summary>
    private List<CopyRange> Expand(ClonePlan plan, IBlockDevice disk, VolumeSources volumes)
    {
        var ranges = new List<CopyRange>();
        foreach (var step in plan.Steps)
        {
            var before = ranges.Count;
            switch (step.Kind)
            {
                case CloneStepKind.Copy:
                    ranges.Add(new CopyRange(step, disk, step.ReadOffset, step.Offset, step.Length));
                    break;

                case CloneStepKind.CopyPartition:
                    ExpandPartition(step, disk, volumes, ranges);
                    break;

                case CloneStepKind.CopyFiles:
                    // Se copia por archivos con el clon montado, después de la copia por bloques.
                    break;

                default:
                    // Borrados y tablas: el contenido se genera al producir los bloques.
                    ranges.Add(new CopyRange(step, null, 0, step.Offset, step.Length));
                    break;
            }

            var added = ranges.Skip(before).ToList();
            var bytes = added.Sum(r => r.Length);
            var description = step.Description;
            CloneLog.StepExpanded(_log, description, bytes, added.Count);
        }

        return ranges;
    }

    private static void ExpandPartition(CloneStep step, IBlockDevice disk, VolumeSources volumes, List<CopyRange> ranges)
    {
        var partition = step.Partition!;
        var partitionStart = partition.OffsetBytes;
        if (!volumes.ByPartition.TryGetValue(partition, out var volume))
        {
            ranges.Add(new CopyRange(step, disk, step.ReadOffset, step.Offset, step.Length));
            return;
        }

        // Tramos relativos al principio de la partición; en el destino empieza en targetStart.
        var from = step.ReadOffset - partitionStart;
        var to = from + step.Length;
        var targetStart = step.Offset - from;
        var deviceEnd = volume.Length;
        var used = step.UsedBlocksOnly ? volume.ReadUsedRanges() : null;

        // Con copia inteligente, lo que la instantánea no deja leer (el final de la partición,
        // fuera de los clústeres de NTFS) se copia igualmente, desde el disco.
        IEnumerable<ByteRange> wanted = used is null
            ? [new ByteRange(0, partition.SizeBytes)]
            : [.. used, new ByteRange(deviceEnd, partition.SizeBytes - deviceEnd)];

        foreach (var range in wanted)
        {
            var start = Math.Max(range.Offset, from);
            var end = Math.Min(range.End, to);
            if (end <= start)
            {
                continue;
            }

            // Lo que cae dentro del volumen se lee de él; si la partición es mayor, el resto, del disco.
            var inVolumeEnd = Math.Min(end, deviceEnd);
            if (inVolumeEnd > start)
            {
                ranges.Add(new CopyRange(step, volume.Device, start, targetStart + start, inVolumeEnd - start));
            }

            var rawStart = Math.Max(start, deviceEnd);
            if (end > rawStart)
            {
                ranges.Add(new CopyRange(step, disk, partitionStart + rawStart, targetStart + rawStart, end - rawStart));
            }
        }
    }

    private static void CheckDevices(ClonePlan plan, IBlockDevice source, IBlockDevice target)
    {
        if (source.Length != plan.Source.SizeBytes || target.Length != plan.Target.SizeBytes)
        {
            throw new CloneException("La capacidad de los discos no coincide con la del plan.");
        }

        if (source.SectorSize != target.SectorSize || source.SectorSize != plan.Source.SectorSize)
        {
            throw new CloneException("El tamaño de sector de los discos no coincide con el del plan.");
        }

        if (source.CanWrite || !target.CanWrite)
        {
            throw new InvalidOperationException("El origen debe abrirse en solo lectura y el destino en escritura.");
        }
    }

    /// <summary>Lee el principio del origen y comprueba que su tabla sigue siendo la del plan.</summary>
    private static byte[] ReadSourceHead(IBlockDevice source, ClonePlan plan)
    {
        var head = ReadBytes(source, 0, PartitionTable.HeadBytes);
        var table = PartitionTable.Read(head, source.SectorSize);
        if (table != plan.SourceTable)
        {
            throw new CloneException($"La tabla de particiones del disco {plan.Source.Number} ha cambiado desde que se revisó el plan.");
        }

        return head;
    }

    /// <summary>Último control de identidad del destino (RS-04): su tabla actual debe ser la que vio el usuario.</summary>
    private static void CheckTargetTable(IBlockDevice target, ClonePlan plan)
    {
        var table = PartitionTable.Read(ReadBytes(target, 0, PartitionTable.HeadBytes), target.SectorSize);
        if (table.Style != plan.Target.PartitionStyle
            || !string.Equals(table.Identifier, plan.Target.Identifier, StringComparison.OrdinalIgnoreCase))
        {
            throw new CloneException($"El disco {plan.Target.Number} ya no es el que se eligió: su tabla de particiones no coincide.");
        }
    }

    private List<WrittenChunk> Copy(
        ClonePlan plan,
        List<CopyRange> ranges,
        IBlockDevice source,
        IBlockDevice target,
        byte[] head,
        BuiltTable? built,
        VolumeSources volumes,
        RunState state,
        ProgressMeter meter,
        IProgress<CloneProgress>? progress,
        CancellationToken cancellation)
    {
        var written = new List<WrittenChunk>();
        var overall = new XxHash128();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var token = linked.Token;

        var buffers = new List<AlignedBuffer>();
        try
        {
            for (var i = 0; i < BufferCount; i++)
            {
                buffers.Add(new AlignedBuffer(ChunkBytes));
            }

            using var zeros = new AlignedBuffer(ChunkBytes);
            using var free = new BlockingCollection<AlignedBuffer>(new ConcurrentQueue<AlignedBuffer>(buffers));
            using var full = new BlockingCollection<Chunk>(BufferCount);

            Exception? readError = null;
            var reader = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        Produce(plan, ranges, source, head, built, volumes, zeros, free, full, written, overall, token);
                    }
                    catch (Exception ex)
                    {
                        readError = ex;
                        linked.Cancel();
                    }
                    finally
                    {
                        full.CompleteAdding();
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Exception? writeError = null;
            try
            {
                var nextLog = 0.1;
                foreach (var chunk in full.GetConsumingEnumerable(token))
                {
                    state.TargetModified = true;
                    target.Write(chunk.Offset, chunk.Buffer.Span[..chunk.Length]);
                    if (!chunk.IsClear)
                    {
                        state.BytesWritten += chunk.Length;
                        free.Add(chunk.Buffer, token);
                    }

                    meter.Advance(chunk.Length);
                    if (meter.ShouldReport())
                    {
                        progress?.Report(meter.Snapshot(ClonePhase.Copying, chunk.Item));
                    }

                    if ((double)meter.Done / meter.Total >= nextLog)
                    {
                        CloneLog.Progress(_log, (double)meter.Done / meter.Total);
                        nextLog += 0.1;
                    }
                }
            }
            catch (Exception ex)
            {
                writeError = ex;
                linked.Cancel();
            }

            reader.Wait(CancellationToken.None);

            // Un error real tiene prioridad sobre la cancelación que provocó en el otro hilo.
            var error = new[] { readError, writeError }.FirstOrDefault(e => e is not null and not OperationCanceledException)
                ?? readError ?? writeError;
            if (error is not null)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }

            cancellation.ThrowIfCancellationRequested();
            state.DataHash = Convert.ToHexStringLower(overall.GetCurrentHash());
            return written;
        }
        finally
        {
            foreach (var buffer in buffers)
            {
                buffer.Dispose();
            }
        }
    }

    /// <summary>Hilo lector: recorre los tramos y entrega bloques listos para escribir.</summary>
    private void Produce(
        ClonePlan plan,
        List<CopyRange> ranges,
        IBlockDevice source,
        byte[] head,
        BuiltTable? built,
        VolumeSources volumes,
        AlignedBuffer zeros,
        BlockingCollection<AlignedBuffer> free,
        BlockingCollection<Chunk> full,
        List<WrittenChunk> written,
        XxHash128 overall,
        CancellationToken token)
    {
        var targetSectors = plan.Target.SizeBytes / plan.Target.SectorSize;
        CloneStep? current = null;

        foreach (var range in ranges)
        {
            var step = range.Step;
            if (!ReferenceEquals(step, current))
            {
                current = step;
                CloneLog.StepStarted(_log, step.Description, step.Offset, step.End);
            }

            for (var offset = range.TargetOffset; offset < range.End; offset += ChunkBytes)
            {
                token.ThrowIfCancellationRequested();
                var length = (int)Math.Min(ChunkBytes, range.End - offset);

                if (step.Kind == CloneStepKind.Clear)
                {
                    full.Add(new Chunk(zeros, length, offset, IsClear: true, step.Description), token);
                    continue;
                }

                var buffer = free.Take(token);
                var data = buffer.Span[..length];
                var item = step.Description;
                switch (step.Kind)
                {
                    case CloneStepKind.Copy:
                        range.Source!.Read(range.SourceOffset + (offset - range.TargetOffset), data);
                        item = DescribeOffset(plan.SourcePartitions, offset) ?? step.Description;
                        break;

                    case CloneStepKind.CopyPartition:
                        range.Source!.Read(range.SourceOffset + (offset - range.TargetOffset), data);
                        if (step.Moves)
                        {
                            PatchBootSectors(step, offset, data, plan.Source.SectorSize);
                        }

                        item = DescribePartition(step.Partition!);
                        break;

                    case CloneStepKind.WriteBackupGpt:
                        var backup = built?.Backup ?? PartitionTable.BuildBackupGpt(head, plan.SourceTable.Gpt!, targetSectors);
                        if (backup.Length != step.Length || length != step.Length)
                        {
                            throw new InvalidOperationException("El tamaño de la GPT de respaldo no coincide con el plan.");
                        }

                        backup.CopyTo(data);
                        break;

                    case CloneStepKind.WritePartitionTable:
                        if (length != step.Length)
                        {
                            throw new InvalidOperationException("La tabla de particiones debe escribirse en un solo bloque.");
                        }

                        // Se vuelve a leer al final y se exige que la tabla no haya cambiado durante la copia.
                        source.Read(offset, data);
                        var tableBytes = PartitionTable.TableBytes(plan.SourceTable, source.SectorSize);
                        if (!data[..tableBytes].SequenceEqual(head.AsSpan(0, tableBytes)))
                        {
                            throw new CloneException("La tabla de particiones del origen ha cambiado durante la copia.");
                        }

                        if (built is not null)
                        {
                            built.Head.CopyTo(data);
                        }
                        else if (plan.AdjustsGpt)
                        {
                            PartitionTable.AdjustPrimaryGpt(data, plan.SourceTable.Gpt!, targetSectors);
                        }

                        OverlayPartitionHeads(plan, source, volumes, data);
                        break;
                }

                written.Add(new WrittenChunk(offset, length, XxHash3.HashToUInt64(data)));
                overall.Append(data);
                full.Add(new Chunk(buffer, length, offset, IsClear: false, item), token);
            }
        }
    }

    /// <summary>
    /// La parte de cada partición que cae dentro del primer MiB del destino se escribe con la
    /// tabla, al final (RS-05). Se lee de la misma fuente que el resto de la partición (su
    /// instantánea, si la tiene), para que el clon sea coherente.
    /// </summary>
    private static void OverlayPartitionHeads(ClonePlan plan, IBlockDevice disk, VolumeSources volumes, Span<byte> data)
    {
        var sectorSize = plan.Source.SectorSize;
        var headLength = data.Length;
        foreach (var placement in plan.Layout.Placements.Where(p => p.TargetOffset < headLength))
        {
            var partition = placement.Partition;
            var length = (int)Math.Min(placement.TargetSize, data.Length - placement.TargetOffset);
            if (placement.Shrinks)
            {
                // Se formatea después: no debe quedar el sistema de archivos del origen, con otro tamaño.
                data.Slice((int)placement.TargetOffset, length).Clear();
                continue;
            }

            length = (int)Math.Min(partition.SizeBytes, data.Length - placement.TargetOffset);
            using var buffer = new AlignedBuffer(length);
            if (volumes.ByPartition.TryGetValue(partition, out var volume) && volume.Length >= length)
            {
                volume.Device.Read(0, buffer.Span);
            }
            else
            {
                disk.Read(partition.OffsetBytes, buffer.Span);
            }

            var slice = data.Slice((int)placement.TargetOffset, length);
            buffer.Span.CopyTo(slice);
            if (placement.Moves)
            {
                BootSector.SetHiddenSectors(slice[..sectorSize], placement.TargetOffset / sectorSize);
            }
        }
    }

    /// <summary>
    /// Una partición movida guarda en su sector de arranque (y en la copia del final) dónde
    /// empieza: se actualiza al copiar el bloque que lo contiene.
    /// </summary>
    private static void PatchBootSectors(CloneStep step, long chunkOffset, Span<byte> data, int sectorSize)
    {
        var partition = step.Partition!;
        var targetStart = step.Offset - (step.ReadOffset - partition.OffsetBytes);
        foreach (var relative in new[] { 0L, partition.SizeBytes - sectorSize })
        {
            var position = targetStart + relative;
            if (position >= chunkOffset && position + sectorSize <= chunkOffset + data.Length)
            {
                BootSector.SetHiddenSectors(data.Slice((int)(position - chunkOffset), sectorSize), targetStart / sectorSize);
            }
        }
    }

    /// <summary>
    /// Trabajo con el clon montado (RF-13, RF-18): ampliar los NTFS que crecen, rehacer el
    /// arranque de un clon de Windows con otros identificadores o posiciones y, si se copió con
    /// identificadores provisionales, poner los definitivos.
    /// </summary>
    private void FinishClone(
        ClonePlan plan,
        IBlockDevice target,
        byte[] head,
        bool temporary,
        VolumeSources sources,
        RunState state,
        ProgressMeter meter,
        IProgress<CloneProgress>? progress,
        CancellationToken cancellation)
    {
        var finisher = _access.Finisher;
        var sectorSize = plan.Source.SectorSize;
        var targetSectors = plan.Target.SizeBytes / plan.Target.SectorSize;

        void Say(string item) => progress?.Report(meter.Snapshot(ClonePhase.Finishing, item));

        Say("Esperando a que Windows monte el clon");
        target.Flush();
        target.RefreshLayout();
        var createdEfi = plan.Layout.Created.FirstOrDefault(c => c.Kind == PartitionKind.EfiSystem);
        var offsets = plan.Layout.Placements.Where(p => p.Partition.Volume is not null).Select(p => p.TargetOffset).ToList();
        if (createdEfi is not null)
        {
            offsets.Add(createdEfi.Offset);
        }

        var volumes = finisher.WaitForVolumes(plan.Target.Number, offsets);
        CloneLog.CloneMounted(_log, volumes.Count);
        if (temporary)
        {
            // En GPT la tabla provisional ya lo evita; en MBR se quitan en cuanto aparecen.
            finisher.RemoveDriveLetters(volumes.Values);
        }

        if (createdEfi is not null)
        {
            Say("Formateando la partición EFI nueva");
            finisher.FormatVolume(volumes[createdEfi.Offset], "EFI", "FAT32");
            CloneLog.VolumeFormatted(_log, "Partición EFI nueva", createdEfi.Size);
        }

        if (plan.FileSteps.Count > 0)
        {
            try
            {
                CopyFileSteps(plan, volumes, sources, state, meter, progress, cancellation);
            }
            catch
            {
                InvalidateMounted(plan, target, volumes.Values, meter, progress);
                throw;
            }

            sources.Release(completed: true);
            state.TargetComplete = true;
        }

        foreach (var placement in plan.Layout.Placements.Where(p => p.Grows))
        {
            var name = DescribePartition(placement.Partition);
            Say($"Ampliando {name}");
            finisher.ExtendVolume(volumes[placement.TargetOffset], placement.TargetSize, sectorSize);
            CloneLog.VolumeExtended(_log, name, placement.TargetSize);
        }

        var windows = finisher.FindWindowsVolume(volumes.Values);
        var converting = plan.Layout.Style is not null;
        var mbrMoves = plan.SourceTable.Gpt is null && plan.Layout.Placements.Any(p => p.Moves);
        if (windows is not null && (plan.NewIdentity is not null || mbrMoves || converting))
        {
            Say("Rehaciendo el arranque del clon");
            var uefi = plan.TargetStyle == PartitionStyle.Gpt;

            // UEFI arranca de la partición EFI (la del origen o la nueva); BIOS, de la activa, que
            // al convertir a MBR es la de Windows.
            long? systemOffset = uefi
                ? plan.Layout.Placements.FirstOrDefault(p => p.Partition.Kind == PartitionKind.EfiSystem)?.TargetOffset ?? createdEfi?.Offset
                : plan.Layout.Placements.FirstOrDefault(p => converting ? p.Partition.ContainsWindows : p.Partition.IsActive)?.TargetOffset;
            var system = systemOffset is { } offset && volumes.TryGetValue(offset, out var path) ? path : windows;
            var map = MountedDevices.Map(head, plan.SourceTable, plan.Layout, plan.NewIdentity, sectorSize);
            finisher.RepairBoot(new BootRepair(windows, system, uefi, map));
            CloneLog.BootRepaired(_log, map.Count);
        }

        if (temporary)
        {
            WaitQuietly(meter, progress, "antes de poner los identificadores definitivos");
            Say("Poniendo los identificadores definitivos");
            var refs = volumes.Values.Select(v => new VolumeRef(v, "del clon")).ToList();
            using (_access.LockVolumes(refs, dismount: true))
            {
                var final = TableBuilder.Build(head, plan.SourceTable, plan.Layout, targetSectors, sectorSize, plan.NewIdentity);
                if (final.Backup is { } backup)
                {
                    WriteChecked(target, target.Length - backup.Length, backup);
                }

                // Solo cambian los bytes de la tabla; el resto del primer MiB ya tiene sus datos.
                var tableBytes = TableBuilder.TableBytes(plan.SourceTable, plan.Layout, sectorSize);
                var current = ReadBytes(target, 0, PartitionTable.HeadBytes);
                final.Head.AsSpan(0, tableBytes).CopyTo(current);
                WriteChecked(target, 0, current);
                target.Flush();
            }

            target.RefreshLayout();
            CloneLog.FinalIdentity(_log);
        }
    }

    /// <summary>
    /// Particiones reducidas (RF-14): formatea cada una y copia en ella los archivos de su volumen,
    /// leídos de la instantánea. Mientras dura, el volumen se llama «ClonDisk incompleto».
    /// </summary>
    private void CopyFileSteps(
        ClonePlan plan,
        IReadOnlyDictionary<long, string> volumes,
        VolumeSources sources,
        RunState state,
        ProgressMeter meter,
        IProgress<CloneProgress>? progress,
        CancellationToken cancellation)
    {
        var finisher = _access.Finisher;
        foreach (var step in plan.FileSteps)
        {
            var partition = step.Partition!;
            var name = DescribePartition(partition);
            var targetVolume = volumes[step.Offset];
            progress?.Report(meter.Snapshot(ClonePhase.Copying, $"Formateando {name} en el destino"));
            finisher.FormatVolume(targetVolume, IncompleteLabel);
            CloneLog.VolumeFormatted(_log, name, step.Length);

            var sourceRoot = step.FromSnapshot ? sources.Snapshots!.RootFor(partition.Volume!.Path) : partition.Volume!.Path;
            var exclusions = plan.SourceIsSystemDisk ? CopyExclusions.ForVolume(partition.DriveLetter) : CopyExclusions.Default();
            long reported = 0;
            var result = finisher.CopyFiles(sourceRoot, targetVolume, exclusions, (bytes, path) =>
            {
                meter.Advance(bytes - reported);
                reported = bytes;
                if (meter.ShouldReport())
                {
                    progress?.Report(meter.Snapshot(ClonePhase.Copying, $"{name}: {path}"));
                }
            }, cancellation);

            // Lo estimado era lo usado del volumen; se ajusta a lo que de verdad se copió.
            meter.Advance(Math.Max(0, step.ExpectedBytes - reported));
            state.BytesWritten += result.Bytes;
            state.FileErrors += result.Errors.Count;
            CloneLog.FilesCopied(_log, name, result.Files, result.Directories, result.HardLinks, result.Bytes, result.Skipped, result.Errors.Count);
            foreach (var error in result.Errors)
            {
                CloneLog.FileNotCopied(_log, error);
            }

            // Sin la etiqueta el clon sigue siendo válido: un fallo aquí solo se anota.
            try
            {
                finisher.SetVolumeLabel(targetVolume, partition.Volume.Label ?? string.Empty);
            }
            catch (CloneException ex)
            {
                CloneLog.LabelFailed(_log, ex.Message);
            }
        }
    }

    /// <summary>
    /// Si la copia por archivos falla o se cancela, el destino se deja sin tabla. Antes hay que
    /// desmontar los volúmenes del clon (y esperar por volsnap) para poder escribir en ellos.
    /// </summary>
    private void InvalidateMounted(ClonePlan plan, IBlockDevice target, IEnumerable<string> volumes, ProgressMeter meter, IProgress<CloneProgress>? progress)
    {
        try
        {
            WaitQuietly(meter, progress, "antes de dejar el destino sin tabla");
            var refs = volumes.Select(v => new VolumeRef(v, "del clon")).ToList();
            using (_access.LockVolumes(refs, dismount: true))
            {
                Invalidate(plan, target);
            }
        }
        catch (Exception ex) when (ex is IOException or CloneException or UnauthorizedAccessException)
        {
            CloneLog.InvalidateFailed(_log, ex);
        }
    }

    /// <summary>
    /// Pausa de seguridad antes de desmontar los volúmenes del clon (incidente de volsnap del
    /// 14/11/2026). No se puede saltar, ni al cancelar, así que se muestra la cuenta atrás.
    /// </summary>
    private void WaitQuietly(ProgressMeter meter, IProgress<CloneProgress>? progress, string purpose)
    {
        CloneLog.QuietWait(_log);
        _access.Finisher.WaitBeforeRemovingVolumes(seconds =>
            progress?.Report(meter.Snapshot(ClonePhase.Finishing, $"Pausa de seguridad {purpose}: {seconds} s")));
    }

    /// <summary>Escribe y relee para comprobar que el destino guarda exactamente esos bytes.</summary>
    private static void WriteChecked(IBlockDevice target, long offset, byte[] data)
    {
        using var buffer = new AlignedBuffer(data.Length);
        data.CopyTo(buffer.Span);
        target.Write(offset, buffer.Span);
        buffer.Span.Clear();
        target.Read(offset, buffer.Span);
        if (!buffer.Span.SequenceEqual(data))
        {
            throw new CloneException($"La tabla de particiones escrita en el byte {offset} no coincide al releerla.");
        }
    }

    /// <summary>Relee lo escrito y compara la huella de cada bloque. Devuelve el primer bloque distinto.</summary>
    private static long? Verify(
        IBlockDevice target,
        List<WrittenChunk> written,
        ProgressMeter meter,
        IProgress<CloneProgress>? progress,
        CancellationToken cancellation)
    {
        using var buffer = new AlignedBuffer(ChunkBytes);
        foreach (var chunk in written)
        {
            cancellation.ThrowIfCancellationRequested();
            var data = buffer.Span[..chunk.Length];
            target.Read(chunk.Offset, data);
            if (XxHash3.HashToUInt64(data) != chunk.Hash)
            {
                return chunk.Offset;
            }

            meter.Advance(chunk.Length);
            if (meter.ShouldReport())
            {
                progress?.Report(meter.Snapshot(ClonePhase.Verifying, "Comparando con lo copiado"));
            }
        }

        return null;
    }

    /// <summary>Vuelve a borrar la tabla y el final del destino para que no parezca un disco válido.</summary>
    private void Invalidate(ClonePlan plan, IBlockDevice target)
    {
        using var zeros = new AlignedBuffer(ChunkBytes);
        foreach (var step in plan.Steps.Where(s => s.Kind == CloneStepKind.Clear))
        {
            for (var offset = step.Offset; offset < step.End; offset += ChunkBytes)
            {
                target.Write(offset, zeros.Span[..(int)Math.Min(ChunkBytes, step.End - offset)]);
            }
        }

        target.Flush();
        CloneLog.TargetInvalidated(_log);
    }

    private void RefreshLayout(IBlockDevice target)
    {
        try
        {
            target.Flush();
            target.RefreshLayout();
        }
        catch (IOException ex)
        {
            CloneLog.RefreshFailed(_log, ex);
        }
    }

    private CloneResult Finish(RunState state, long started, CloneOutcome outcome, string message)
    {
        var result = new CloneResult
        {
            Outcome = outcome,
            Message = message,
            TargetModified = state.TargetModified,
            TargetComplete = state.TargetComplete,
            Verified = state.Verified,
            BytesWritten = state.BytesWritten,
            Elapsed = _time.GetElapsedTime(started),
            DataHash = state.DataHash,
        };

        var level = outcome == CloneOutcome.Succeeded ? LogLevel.Information : LogLevel.Warning;
        CloneLog.Finished(_log, level, outcome, message, result.Elapsed);
        return result;
    }

    private void LogPlan(ClonePlan plan)
    {
        var source = plan.Source.Describe(Culture);
        var target = plan.Target.Describe(Culture);
        var mode = CloneText.Mode(plan.Mode);
        CloneLog.PlanStarted(_log, mode, plan.Options.Verify ? "sí" : "no", plan.AdjustsGpt ? "sí" : "no");
        CloneLog.PlanDisk(_log, "Origen", source, plan.Source.SerialNumber, plan.Source.Identifier, plan.Source.Location);
        CloneLog.PlanDisk(_log, "Destino", target, plan.Target.SerialNumber, plan.Target.Identifier, plan.Target.Location);
        foreach (var partition in plan.TargetPartitions)
        {
            var description = DescribePartition(partition);
            CloneLog.TargetPartitionErased(_log, description);
        }

        foreach (var warning in plan.Warnings)
        {
            CloneLog.PlanWarning(_log, warning);
        }
    }

    private static List<VolumeRef> Volumes(IReadOnlyList<PartitionInfo> partitions) =>
        partitions
            .Where(p => p.Volume is not null)
            .Select(p => new VolumeRef(p.Volume!.Path, p.DriveLetter is { } letter ? $"{letter}:" : p.Volume.Label ?? $"de la partición {p.Number}"))
            .ToList();

    private static string? DescribeOffset(IReadOnlyList<PartitionInfo> partitions, long offset)
    {
        var partition = partitions.FirstOrDefault(p => p.Kind != PartitionKind.Extended && offset >= p.OffsetBytes && offset < p.OffsetBytes + p.SizeBytes);
        return partition is null ? "Espacio sin particionar" : DescribePartition(partition);
    }

    private static string DescribePartition(PartitionInfo partition)
    {
        var name = partition.DriveLetter is { } letter
            ? partition.Volume?.Label is { } label ? $"{letter}: {label}" : $"{letter}:"
            : partition.Volume?.Label ?? InventoryText.Kind(partition.Kind);
        return $"Partición {partition.Number} ({name})";
    }

    private static byte[] ReadBytes(IBlockDevice device, long offset, int length)
    {
        using var buffer = new AlignedBuffer(length);
        device.Read(offset, buffer.Span);
        return buffer.Span.ToArray();
    }

    private sealed class RunState
    {
        public ClonePhase Phase { get; set; } = ClonePhase.Preparing;
        public bool TargetModified { get; set; }
        public bool TargetComplete { get; set; }
        public bool Verified { get; set; }
        public long BytesWritten { get; set; }
        public string? DataHash { get; set; }
        public int FileErrors { get; set; }
    }

    private readonly record struct Chunk(AlignedBuffer Buffer, int Length, long Offset, bool IsClear, string Item);

    /// <summary>Un tramo concreto: se lee de <paramref name="Source"/> (nulo si se genera) y se escribe en el destino.</summary>
    private sealed record CopyRange(CloneStep Step, IBlockDevice? Source, long SourceOffset, long TargetOffset, long Length)
    {
        public long End => TargetOffset + Length;
    }

    /// <summary>Instantáneas y volúmenes abiertos durante la copia; se liberan en orden.</summary>
    private sealed class VolumeSources
    {
        public ISnapshotSet? Snapshots { get; set; }
        public Dictionary<PartitionInfo, IVolumeSource> ByPartition { get; } = [];

        /// <summary>Cierra los volúmenes abiertos y conserva las instantáneas.</summary>
        public void ReleaseDevices()
        {
            foreach (var volume in ByPartition.Values)
            {
                volume.Dispose();
            }

            ByPartition.Clear();
        }

        public void Release(bool completed)
        {
            ReleaseDevices();
            if (Snapshots is not null)
            {
                if (completed)
                {
                    Snapshots.Complete();
                }

                Snapshots.Dispose();
                Snapshots = null;
            }
        }
    }

    /// <summary>Varios bloqueos que se liberan juntos.</summary>
    private sealed class CompositeLock : IDisposable
    {
        private readonly List<IDisposable> _locks = [];

        public void Add(IDisposable item) => _locks.Add(item);

        public void Dispose()
        {
            foreach (var item in _locks)
            {
                item.Dispose();
            }

            _locks.Clear();
        }
    }

    private readonly record struct WrittenChunk(long Offset, int Length, ulong Hash);
}
