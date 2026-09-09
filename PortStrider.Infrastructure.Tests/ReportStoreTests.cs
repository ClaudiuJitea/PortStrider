using PortStrider.Core.Models;
using PortStrider.Infrastructure.Reports;
using Xunit;

namespace PortStrider.Infrastructure.Tests;

public sealed class ReportStoreTests
{
    [Fact]
    public async Task ExportPdf_WritesValidPdfEnvelope()
    {
        var path = Path.Combine(Path.GetTempPath(), $"portstrider-{Guid.NewGuid():N}.pdf");
        try
        {
            var report = new SessionReport
            {
                Id = Guid.NewGuid(),
                StartedAt = DateTimeOffset.UtcNow,
                AdapterName = "eth0",
                ProfileName = "Default",
                Overall = "PASS",
                Steps =
                [
                    new TestStepUpdate
                    {
                        Id = "link",
                        Title = "Physical link",
                        Status = Core.Enums.TestStatus.Pass,
                        Details = "1 Gbps"
                    }
                ]
            };

            await new JsonReportStore().ExportPdfAsync(report, path);

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.StartsWith("%PDF-1.4", System.Text.Encoding.ASCII.GetString(bytes));
            Assert.EndsWith("%%EOF\n", System.Text.Encoding.ASCII.GetString(bytes));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
