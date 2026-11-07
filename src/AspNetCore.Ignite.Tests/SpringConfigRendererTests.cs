using AspNetCore.IgniteServer.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace AspNetCore.Ignite.Tests;

[TestClass]
public class SpringConfigRendererTests
{
    [TestMethod]
    public void Render_ReplacesAndEscapesPlaceholders()
    {
        var rendered = SpringConfigRenderer.Render(
            "<a ns=\"K8S_NAMESPACE\" pwd=\"SSL_KEY_STORE_PASSWORD\" svc=\"K8S_SERVICE_NAME\"/>",
            new Dictionary<string, string?>
            {
                ["K8S_NAMESPACE"] = "prod",
                ["SSL_KEY_STORE_PASSWORD"] = "a&b<\"c\">",
                ["K8S_SERVICE_NAME"] = null,
            }
        );

        Assert.AreEqual("<a ns=\"prod\" pwd=\"a&amp;b&lt;&quot;c&quot;&gt;\" svc=\"\"/>", rendered);
    }

    [TestMethod]
    public async Task RenderToFileAsync_LeavesTemplateUntouched()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spring-config-renderer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var templatePath = Path.Combine(directory, "spring-config.xml");
            const string template = "<p value=\"K8S_NAMESPACE\"/>";
            await File.WriteAllTextAsync(templatePath, template);
            var outputDirectory = Path.Combine(directory, "out");

            var first = await SpringConfigRenderer.RenderToFileAsync(
                templatePath,
                new Dictionary<string, string?> { ["K8S_NAMESPACE"] = "first", },
                outputDirectory
            );
            var second = await SpringConfigRenderer.RenderToFileAsync(
                templatePath,
                new Dictionary<string, string?> { ["K8S_NAMESPACE"] = "second", },
                outputDirectory
            );

            Assert.AreEqual(template, await File.ReadAllTextAsync(templatePath));
            Assert.AreEqual(first, second);
            Assert.AreEqual("<p value=\"second\"/>", await File.ReadAllTextAsync(second));
            if (!OperatingSystem.IsWindows())
            {
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(second));
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
