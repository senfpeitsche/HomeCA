using HomeCA.Service.Endpoints;

namespace HomeCA.Tests;

public sealed class CertificateEndpointGuardTests
{
    [Theory]
    [InlineData("dcfff0da4113ad3924f871e4bbab3955", true)]
    [InlineData("0A1B", true)]
    [InlineData("..", false)]
    [InlineData("..\\..\\state", false)]
    [InlineData("abc/def", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_Hex_Ids_Reach_The_File_System(string? id, bool expected) =>
        Assert.Equal(expected, CertificateEndpoints.IsCertificateId(id));
}
