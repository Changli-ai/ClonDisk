using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Hashing;
using System.Runtime.ExceptionServices;
using Cloner.Core.Devices;
using Cloner.Core.Inventory;
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
        var verifyBytes = plan.Options.Verify ? plan.BytesToCopy : 0;
        var meter = new ProgressMeter(plan.Steps.Sum(s => s.Length) + verifyBytes, _time);
        var state = new RunState();
        LogPlan(plan);

        IBlockDevice? source = null;
        IBlockDevice? target = null;
        try
        {
            progress?.Report(meter.Snapshot(ClonePhase.Preparing, "Comprobando los discos"));
            source = _access.Open(plan.Source, writable: false);
            target = _access.Open(plan.Target, writable: true);
            CheckDevices(plan, source, target);
            var head = ReadSourceHead(source, plan);
            CheckTargetTable(target, plan);
            cancellation.ThrowIfCancellationRequested();

            using (_access.LockVolumes(Volumes(plan.SourcePartitions), dismount: false))
            using (_access.LockVolumes(Volumes(plan.TargetPartitions), dismount: true))
            {
                CloneLog.VolumesLocked(_log);
                state.Phase = ClonePhase.Copying;
                var written = Copy(plan, source, target, head, state, meter, progress, cancellation);
                target.Flush();
                state.TargetComplete = true;
                CloneLog.CopyFinished(_log, state.BytesWritten, state.DataHash);

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
            progress?.Report(meter.Snapshot(ClonePhase.Finishing, "Terminando"));
            return Finish(state, started, CloneOutcome.Succeeded, state.Verified
                ? "El destino es idéntico a lo leído del origen."
                : "La copia ha terminado, pero no se ha verificado.");
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
            var message = reason.TrimEnd('.') + "." + (state.TargetModified
                ? " El destino ha quedado incompleto y sin tabla de particiones."
                : " No se ha escrito nada en el destino.");
            return Finish(state, started, CloneOutcome.Failed, message);
        }
        finally
        {
            if (target is not null && state.TargetModified)
            {
                RefreshLayout(target);
            }

            target?.Dispose();
            source?.Dispose();
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
        IBlockDevice source,
        IBlockDevice target,
        byte[] head,
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
                        Produce(plan, source, head, zeros, free, full, written, overall, token);
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

    /// <summary>Hilo lector: recorre los pasos y entrega bloques listos para escribir.</summary>
    private void Produce(
        ClonePlan plan,
        IBlockDevice source,
        byte[] head,
        AlignedBuffer zeros,
        BlockingCollection<AlignedBuffer> free,
        BlockingCollection<Chunk> full,
        List<WrittenChunk> written,
        XxHash128 overall,
        CancellationToken token)
    {
        var targetSectors = plan.Target.SizeBytes / plan.Target.SectorSize;

        foreach (var step in plan.Steps)
        {
            CloneLog.StepStarted(_log, step.Description, step.Offset, step.End);

            for (var offset = step.Offset; offset < step.End; offset += ChunkBytes)
            {
                token.ThrowIfCancellationRequested();
                var length = (int)Math.Min(ChunkBytes, step.End - offset);

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
                        source.Read(offset, data);
                        item = DescribeOffset(plan.SourcePartitions, offset) ?? step.Description;
                        break;

                    case CloneStepKind.WriteBackupGpt:
                        var backup = PartitionTable.BuildBackupGpt(head, plan.SourceTable.Gpt!, targetSectors);
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

                        if (plan.AdjustsGpt)
                        {
                            PartitionTable.AdjustPrimaryGpt(data, plan.SourceTable.Gpt!, targetSectors);
                        }

                        break;
                }

                written.Add(new WrittenChunk(offset, length, XxHash3.HashToUInt64(data)));
                overall.Append(data);
                full.Add(new Chunk(buffer, length, offset, IsClear: false, item), token);
            }
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
        CloneLog.PlanStarted(_log, plan.Options.Verify ? "sí" : "no", plan.AdjustsGpt ? "sí" : "no");
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
    }

    private readonly record struct Chunk(AlignedBuffer Buffer, int Length, long Offset, bool IsClear, string Item);

    private readonly record struct WrittenChunk(long Offset, int Length, ulong Hash);
}
