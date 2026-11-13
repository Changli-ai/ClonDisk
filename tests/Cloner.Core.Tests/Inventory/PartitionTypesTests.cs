using Cloner.Core.Inventory;

namespace Cloner.Core.Tests.Inventory;

public class PartitionTypesTests
{
    [Theory]
    [InlineData("{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}", PartitionKind.EfiSystem)]
    [InlineData("E3C9E316-0B5C-4DB8-817D-F92DF00215AE", PartitionKind.MicrosoftReserved)]
    [InlineData("{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}", PartitionKind.BasicData)]
    [InlineData("{de94bba4-06d1-4d40-a16a-bfd50179d6ac}", PartitionKind.Recovery)]
    [InlineData("{af9b60a0-1431-4f62-bc68-3311714a69ad}", PartitionKind.Dynamic)]
    [InlineData("{0fc63daf-8483-4772-8e79-3d69d8477de4}", PartitionKind.Other)] // Linux
    [InlineData("no es un guid", PartitionKind.Unknown)]
    [InlineData(null, PartitionKind.Unknown)]
    public void GptTypesAreClassified(string? gptType, PartitionKind expected)
    {
        Assert.Equal(expected, PartitionTypes.FromGptType(gptType));
    }

    [Theory]
    [InlineData(0x07, PartitionKind.BasicData)]
    [InlineData(0x0C, PartitionKind.BasicData)]
    [InlineData(0x05, PartitionKind.Extended)]
    [InlineData(0x0F, PartitionKind.Extended)]
    [InlineData(0x27, PartitionKind.Recovery)]
    [InlineData(0xEF, PartitionKind.EfiSystem)]
    [InlineData(0x83, PartitionKind.Other)] // Linux
    [InlineData(0x00, PartitionKind.Unknown)]
    public void MbrTypesAreClassified(int mbrType, PartitionKind expected)
    {
        Assert.Equal(expected, PartitionTypes.FromMbrType(mbrType));
    }
}
