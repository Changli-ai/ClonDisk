using Cloner.Core.Cloning;
using static Cloner.Core.Tests.Cloning.DiskImages;

namespace Cloner.Core.Tests.Cloning;

public class VerifyDiagnosisTests
{
    private static byte[] Head() => Gpt(8 * MiB, new Guid("aaaaaaaa-1111-2222-3333-444444444444"), (MiB, 6 * MiB)).AsSpan(0, (int)MiB).ToArray();

    [Fact]
    public void ChangedDiskGuidIsRecognisedAsWindowsResolvingADuplicate()
    {
        var expected = Head();
        var actual = (byte[])expected.Clone();
        Guid.NewGuid().TryWriteBytes(actual.AsSpan(Sector + 56, 16));
        actual[Sector + 16] ^= 0x5A;

        var finding = VerifyDiagnosis.Describe(expected, actual, 0, Sector);

        Assert.Contains("identificadores del disco", finding.Summary, StringComparison.Ordinal);
        Assert.Contains(finding.Details, d => d.Contains("GUID) del disco", StringComparison.Ordinal));
        Assert.Contains(finding.Details, d => d.Contains("CRC de la cabecera", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyHeadMeansTheTableNeverArrived()
    {
        var expected = Head();
        var actual = new byte[expected.Length];

        var finding = VerifyDiagnosis.Describe(expected, actual, 0, Sector);

        Assert.Contains("vacío", finding.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedBootCodeIsBlamedOnMbrProtection()
    {
        var expected = Head();
        var actual = (byte[])expected.Clone();
        actual.AsSpan(10, 50).Fill(0x90);

        var finding = VerifyDiagnosis.Describe(expected, actual, 0, Sector);

        Assert.Contains("código de arranque", finding.Summary, StringComparison.Ordinal);
        Assert.Contains(finding.Details, d => d.Contains("Bytes 10–59", StringComparison.Ordinal));
    }

    [Fact]
    public void ZonesNameTheGptFields()
    {
        Assert.Equal("firma del disco (MBR)", VerifyDiagnosis.Zone(441, Sector));
        Assert.Equal("entrada GPT 1, identificador (GUID) de la partición", VerifyDiagnosis.Zone((2 * Sector) + 20, Sector));
        Assert.Equal("entrada GPT 3, nombre", VerifyDiagnosis.Zone((2 * Sector) + (2 * 128) + 60, Sector));
        Assert.Equal("datos del primer MiB, después de la tabla de particiones", VerifyDiagnosis.Zone(40 * Sector, Sector));
    }

    [Fact]
    public void NearbyDifferencesAreMergedIntoOneRange()
    {
        var expected = new byte[100];
        var actual = (byte[])expected.Clone();
        actual[10] = 1;
        actual[14] = 1;
        actual[60] = 1;

        var ranges = VerifyDiagnosis.Differences(expected, actual);

        Assert.Equal([(10, 5), (60, 1)], ranges);
    }
}
