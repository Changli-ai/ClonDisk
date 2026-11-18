using System.Globalization;
using Cloner.Core.PartitionTables;

namespace Cloner.Core.Cloning;

/// <summary>Explicación de una diferencia encontrada al verificar: un resumen y el detalle para el registro.</summary>
internal sealed record VerifyFinding(string Summary, IReadOnlyList<string> Details);

/// <summary>
/// Explica en qué se diferencia lo releído del destino de lo que se escribió. Para el principio
/// del disco dice qué parte de la tabla cambió (código de arranque, firma o GUID del disco,
/// entradas…) y qué suele significar, para distinguir, por ejemplo, un antivirus que protege el
/// MBR de Windows cambiando el identificador de un disco duplicado. No toca discos.
/// </summary>
internal static class VerifyDiagnosis
{
    private const int MaxRanges = 8;
    private const int MergeGap = 8;
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-ES");

    /// <param name="offset">Posición en el disco del primer byte de <paramref name="expected"/>.</param>
    public static VerifyFinding Describe(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, long offset, int sectorSize)
    {
        var length = Math.Min(expected.Length, actual.Length);
        var ranges = Differences(expected[..length], actual[..length]);
        var differing = ranges.Sum(r => r.Length);
        var details = new List<string>
        {
            $"Bloque de {Bytes(length)} bytes que empieza en el byte {Bytes(offset)}: {Bytes(differing)} bytes distintos en {ranges.Count.ToString(Culture)} tramos.",
        };

        foreach (var (start, count) in ranges.Take(MaxRanges))
        {
            var position = offset + start;
            details.Add($"  Bytes {Bytes(position)}–{Bytes(position + count - 1)}: {Zone(position, sectorSize)}.");
        }

        if (ranges.Count > MaxRanges)
        {
            details.Add($"  … y {(ranges.Count - MaxRanges).ToString(Culture)} tramos más.");
        }

        var summary = offset == 0 && length >= 2 * sectorSize
            ? DescribeHead(expected[..length], actual[..length], ranges, sectorSize, details)
            : $"El bloque que empieza en el byte {Bytes(offset)} no coincide con lo escrito ({Bytes(differing)} bytes distintos).";
        return new VerifyFinding(summary, details);
    }

    /// <summary>Tramos [inicio, longitud) donde difieren, uniendo los separados por pocos bytes iguales.</summary>
    public static List<(int Start, int Length)> Differences(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        var ranges = new List<(int Start, int Length)>();
        var position = 0;
        while (position < expected.Length)
        {
            var start = position + expected[position..].CommonPrefixLength(actual[position..]);
            if (start >= expected.Length)
            {
                break;
            }

            var end = start + 1;
            var same = 0;
            while (end < expected.Length && same <= MergeGap)
            {
                same = expected[end] == actual[end] ? same + 1 : 0;
                end++;
            }

            end -= same;
            if (ranges.Count > 0 && start - (ranges[^1].Start + ranges[^1].Length) <= MergeGap)
            {
                var last = ranges[^1];
                ranges[^1] = (last.Start, end - last.Start);
            }
            else
            {
                ranges.Add((start, end - start));
            }

            position = end;
        }

        return ranges;
    }

    /// <summary>Qué contiene una posición del principio del disco.</summary>
    public static string Zone(long position, int sectorSize)
    {
        var sector = position / sectorSize;
        var relative = (int)(position % sectorSize);
        if (sector == 0)
        {
            return relative switch
            {
                < 440 => "código de arranque del MBR",
                < 444 => "firma del disco (MBR)",
                < 446 => "MBR, bytes reservados",
                < 510 => $"tabla de particiones MBR, entrada {(((relative - 446) / 16) + 1).ToString(Culture)}",
                _ => "marca 55AA del MBR",
            };
        }

        if (sector == 1)
        {
            return relative switch
            {
                < 8 => "firma «EFI PART» de la cabecera GPT",
                >= 16 and < 20 => "CRC de la cabecera GPT",
                >= 56 and < 72 => "identificador (GUID) del disco en la cabecera GPT",
                >= 88 and < 92 => "CRC de las entradas GPT",
                _ => "cabecera GPT",
            };
        }

        if (sector < 34)
        {
            var entryOffset = (int)(position - (2L * sectorSize));
            var entry = (entryOffset / 128) + 1;
            var field = (entryOffset % 128) switch
            {
                < 16 => "tipo",
                < 32 => "identificador (GUID) de la partición",
                < 48 => "posición",
                < 56 => "atributos",
                _ => "nombre",
            };
            return $"entrada GPT {entry.ToString(Culture)}, {field}";
        }

        return position < PartitionTable.HeadBytes
            ? "datos del primer MiB, después de la tabla de particiones"
            : "datos";
    }

    private static string DescribeHead(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, List<(int Start, int Length)> ranges, int sectorSize, List<string> details)
    {
        var expectedGpt = GptHeader.TryParse(expected.Slice(sectorSize, sectorSize));
        var actualGpt = GptHeader.TryParse(actual.Slice(sectorSize, sectorSize));
        if (expectedGpt is not null)
        {
            details.Add($"  GUID del disco escrito: {expectedGpt.DiskGuid}; releído: {(actualGpt is null ? "sin cabecera GPT válida" : actualGpt.DiskGuid.ToString())}.");
        }
        else
        {
            details.Add($"  Firma MBR escrita: {Signature(expected)}; releída: {Signature(actual)}.");
        }

        if (actual[..(2 * sectorSize)].IndexOfAnyExcept((byte)0) < 0)
        {
            return "El principio del destino está vacío al releerlo: la tabla de particiones no llegó a escribirse o alguien la borró después (por ejemplo, la protección del MBR de un antivirus).";
        }

        var zones = ranges.Select(r => Kind(r.Start, r.Length, sectorSize)).Distinct().ToList();
        if (zones.All(z => z is Area.DiskId or Area.Crc or Area.PartitionId))
        {
            return "Solo cambiaron los identificadores del disco: es lo que hace Windows al encontrar dos discos conectados con el mismo identificador (el clon y el original).";
        }

        if (zones.All(z => z == Area.BootCode))
        {
            return "Solo cambió el código de arranque del MBR: suele hacerlo la protección del MBR de un antivirus o un gestor de arranque.";
        }

        if (expectedGpt is not null && actualGpt is null)
        {
            return "Al releerlo, el principio del destino ya no tiene una tabla GPT válida: algo lo sobrescribió después de escribirlo (por ejemplo, un antivirus que protege el MBR).";
        }

        return "La tabla de particiones del principio del destino es distinta de la escrita: algo la modificó después de escribirla.";
    }

    private enum Area
    {
        BootCode,
        DiskId,
        Crc,
        PartitionId,
        Other,
    }

    private static Area Kind(int start, int length, int sectorSize)
    {
        var end = start + length;
        bool Within(int from, int to) => start >= from && end <= to;
        if (Within(0, 440))
        {
            return Area.BootCode;
        }

        if (Within(440, 444) || Within(sectorSize + 56, sectorSize + 72))
        {
            return Area.DiskId;
        }

        if (Within(sectorSize + 16, sectorSize + 20) || Within(sectorSize + 88, sectorSize + 92))
        {
            return Area.Crc;
        }

        if (start >= 2 * sectorSize && end <= 34 * sectorSize)
        {
            var from = (start - (2 * sectorSize)) % 128;
            var to = from + length;
            return from >= 16 && to <= 32 ? Area.PartitionId : Area.Other;
        }

        return Area.Other;
    }

    private static string Signature(ReadOnlySpan<byte> head) =>
        BitConverter.ToUInt32(head.Slice(440, 4)).ToString("X8", CultureInfo.InvariantCulture);

    private static string Bytes(long value) => value.ToString("N0", Culture);
}
